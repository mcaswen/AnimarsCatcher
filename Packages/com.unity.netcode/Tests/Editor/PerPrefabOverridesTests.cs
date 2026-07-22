using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    // TODO：验证根实体全部组件的默认 Variant 均为 DefaultSerialization
    // TODO：验证手动指定的默认 Variant 得到正确应用
    // TODO：验证全部子实体组件的默认 Variant 均为 DontSerializeVariant
    // TODO：补充 ClientOnlyVariant 用法测试

    [TestFixture]
    internal class PerPrefabOverridesTests
    {
        internal class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var transform = baker.GetComponent<Transform>();
                baker.DependsOn(transform.parent);
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                if(transform.parent == null)
                    baker.AddComponent(entity, new GhostOwner { NetworkId = -1});
                baker.AddComponent(entity, new GhostGen_IntStruct());
            }
        }

        GameObject[] CreatePrefabs(string[] names)
        {
            var collection = new GameObject[names.Length];
            for (int i = 0; i < names.Length; ++i)
            {
                var ghostGameObject = new GameObject(names[i]);
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                var childGhost = new GameObject("Child");
                childGhost.transform.parent = ghostGameObject.transform;
                childGhost.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                var nestedChildGhost = new GameObject("NestedChild");
                nestedChildGhost.transform.parent = childGhost.transform;
                nestedChildGhost.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
                authoring.SupportedGhostModes = GhostModeMask.All;
                collection[i] = ghostGameObject;
            }

            return collection;
        }

        // 检查组件 Prefab Serializer 及其索引是否按预期初始化
        void CheckCollection(World world, int serializerIndex, int entityIndex)
        {
            using var collectionQuery = world.EntityManager.CreateEntityQuery(typeof(GhostCollection));
            var collection = collectionQuery.GetSingletonEntity();
            var ghostSerializerCollection = world.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(collection);
            var ghostComponentIndex = world.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collection);
            Assert.AreEqual(4, ghostSerializerCollection.Length);
            // 前三种发送规则 All、Predicted 和 Interpolated 应包含该组件及 GhostGen_IntStruct
            for (int i = 0; i < ghostSerializerCollection.Length; ++i)
            {
                if(serializerIndex != ghostComponentIndex[ghostSerializerCollection[i].FirstComponent].SerializerIndex)
                    continue;
                if (ghostSerializerCollection[i].NumComponents == 5)
                {
                    Assert.AreEqual(1, ghostSerializerCollection[i].NumChildComponents);
                    Assert.AreEqual(2, ghostComponentIndex.AsNativeArray()
                        .GetSubArray(ghostSerializerCollection[i].FirstComponent, 5)
                        .Count(t => t.SerializerIndex == serializerIndex));
                }
                // None 规则对应的 Variant 应只有 4 个组件
                else if (ghostSerializerCollection[i].NumComponents == 4)
                {
                    Assert.AreEqual(entityIndex==0?1:0, ghostSerializerCollection[i].NumChildComponents);
                    Assert.AreEqual(1, ghostComponentIndex.AsNativeArray()
                        .GetSubArray(ghostSerializerCollection[i].FirstComponent, 4)
                        .Count(t => t.SerializerIndex == serializerIndex));
                }
                else
                {
                    Assert.Fail("Invalid number of componenent");
                }
            }
        }

        [Test]
        public void OverrideComponentPrefabType_RootEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"ServerOnly", "ClientOnly", "PredictedOnly", "InterpolatedOnly"};
                var prefabTypes = new[] {GhostPrefabType.Server, GhostPrefabType.Client, GhostPrefabType.InterpolatedClient, GhostPrefabType.PredictedClient};
                var collection = CreatePrefabs(names);
                // 在不同 Prefab 上覆盖根实体组件的 PrefabType
                for (int i = 0; i < prefabTypes.Length; ++i)
                {
                    var gameObject = collection[i];
                    var inspection = gameObject.AddComponent<GhostAuthoringInspectionComponent>();
                    inspection.ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = prefabTypes[i],
                            SendTypeOptimization = GhostSendType.AllClients,
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                // 注册 Serializer 并完成 System 初始化
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 检查服务端与客户端 Prefab 的预期组件集合
                var ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefabList = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    if ((prefabTypes[i] & GhostPrefabType.Server) != 0)
                        Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    else
                        Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    var linkedGroupBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                }

                ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
                prefabList = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    if ((prefabTypes[i] & GhostPrefabType.Client) != 0)
                        Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    else
                        Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    var linkedGroupBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                }
            }
        }

        [Test]
        public void OverrideComponentPrefabType_ChildEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"ServerOnly", "ClientOnly", "PredictedOnly", "InterpolatedOnly"};
                var prefabTypes = new[] {GhostPrefabType.Server, GhostPrefabType.Client, GhostPrefabType.InterpolatedClient, GhostPrefabType.PredictedClient};
                var collection = CreatePrefabs(names);
                // 只覆盖直接子实体的行为
                for (int i = 0; i < prefabTypes.Length; ++i)
                {
                    var gameObject = collection[i];
                    var child = gameObject.transform.GetChild(0);
                    child.gameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = prefabTypes[i],
                            SendTypeOptimization = GhostSendType.AllClients,
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                // 注册 Serializer 并完成 System 初始化
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 检查预期结果
                // 服务端
                var ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefabList = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    var linkedGroupBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    if ((prefabTypes[i] & GhostPrefabType.Server) != 0)
                        Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                    else
                        Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value), "{0} should not have ChildComponent", names[i]);
                }
                // 客户端
                ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
                prefabList = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    var linkedGroupBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    if ((prefabTypes[i] & GhostPrefabType.Client) != 0)
                        Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                    else
                        Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                }
            }
        }

        [Test]
        public void OverrideComponentPrefabType_NestedChildEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"ServerOnly", "ClientOnly", "PredictedOnly", "InterpolatedOnly"};
                var prefabTypes = new[] {GhostPrefabType.Server, GhostPrefabType.Client, GhostPrefabType.InterpolatedClient, GhostPrefabType.PredictedClient};
                var collection = CreatePrefabs(names);
                // 只覆盖嵌套子实体的行为
                for (int i = 0; i < prefabTypes.Length; ++i)
                {
                    var gameObject = collection[i];

                    var child = gameObject.transform.GetChild(0);
                    var nestedChild = child.GetChild(0);
                    nestedChild.gameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = prefabTypes[i],
                            SendTypeOptimization = GhostSendType.AllClients,
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                // 注册 Serializer 并完成 System 初始化
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 检查预期结果
                // 服务端
                var ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefabList = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    var linkedGroupBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    if ((prefabTypes[i] & GhostPrefabType.Server) != 0)
                        Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[2].Value));
                    else
                        Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[2].Value), "{0} should not have ChildComponent", names[i]);
                }
                // 客户端
                ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
                prefabList = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    var linkedGroupBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    if ((prefabTypes[i] & GhostPrefabType.Client) != 0)
                        Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[2].Value));
                    else
                        Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[2].Value));
                }
            }
        }

        [Test]
        public void OverrideComponentSendType_RootEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"All", "Interpolated", "Predicted", "None"};
                var sendTypes = new[] {GhostSendType.AllClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.OnlyPredictedClients, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var inspection = collection[i].AddComponent<GhostAuthoringInspectionComponent>();
                    inspection.ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = GhostPrefabType.All,
                            SendTypeOptimization = sendTypes[i],
                            VariantHash = 0,
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                // 注册 Serializer 并完成 System 初始化
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 进入游戏状态，使 Ghost Collection 完成运行时设置
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 检查各发送规则生成的 Serializer 索引
                var collectionEntity = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collectionEntity);
                var ghostComponentCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentType>(collectionEntity);

                var type = TypeManager.GetTypeIndex(typeof(GhostGen_IntStruct));
                var index = 0;
                while (index < ghostCollection.Length && ghostComponentCollection[ghostCollection[index].ComponentIndex].Type.TypeIndex != type) ++index;
                var serializerIndex = ghostCollection[index].SerializerIndex;


                CheckCollection(testWorld.ServerWorld, serializerIndex, 0);
                CheckCollection(testWorld.ClientWorlds[0], serializerIndex, 0);
            }
        }

        [Test]
        public void OverrideComponentSendType_ChildEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"All", "Interpolated", "Predicted", "None"};
                var sendTypes = new[] {GhostSendType.AllClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.OnlyPredictedClients, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var gameObject = collection[i];
                    var child = gameObject.transform.GetChild(0);
                    child.gameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new[]
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = GhostPrefabType.All,
                            SendTypeOptimization = sendTypes[i],
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                // 注册 Serializer 并完成 System 初始化
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 进入游戏状态，使 Ghost Collection 完成运行时设置
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 检查直接子实体各发送规则生成的 Serializer 索引
                var collectionEntity = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collectionEntity);
                var ghostComponentCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentType>(collectionEntity);

                var type = TypeManager.GetTypeIndex(typeof(GhostGen_IntStruct));
                var index = 0;
                while (index < ghostCollection.Length && ghostComponentCollection[ghostCollection[index].ComponentIndex].Type.TypeIndex != type)
                    ++index;
                var serializerIndex = ghostCollection[index].SerializerIndex;

                CheckCollection(testWorld.ServerWorld, serializerIndex, 1);
                CheckCollection(testWorld.ClientWorlds[0], serializerIndex, 1);
            }
        }

        [Test]
        public void OverrideComponentSendType_NestedChildEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"All", "Interpolated", "Predicted", "None"};
                var sendTypes = new[] {GhostSendType.AllClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.OnlyPredictedClients, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var gameObject = collection[i];
                    var child = gameObject.transform.GetChild(0);
                    var nestedChild = child.GetChild(0);
                    nestedChild.gameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = GhostPrefabType.All,
                            SendTypeOptimization = sendTypes[i],
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                // 注册 Serializer 并完成 System 初始化
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 进入游戏状态，使 Ghost Collection 完成运行时设置
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 检查嵌套子实体各发送规则生成的 Serializer 索引
                var collectionEntity = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collectionEntity);
                var ghostComponentCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentType>(collectionEntity);

                var type = TypeManager.GetTypeIndex(typeof(GhostGen_IntStruct));
                var index = 0;
                while (index < ghostCollection.Length && ghostComponentCollection[ghostCollection[index].ComponentIndex].Type.TypeIndex != type)
                {
                    ++index;
                }
                var serializerIndex = ghostCollection[index].SerializerIndex;

                CheckCollection(testWorld.ServerWorld, serializerIndex, 2);
                CheckCollection(testWorld.ClientWorlds[0], serializerIndex, 2);
            }
        }

        /// <summary>
        /// 可显式指定给 LocalTransform 的测试 Variant
        /// </summary>
        [GhostComponentVariation(typeof(Transforms.LocalTransform), nameof(TransformVariantTest))]
        [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
        internal struct TransformVariantTest
        {
            [GhostField(Quantization=100, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
            public float3 Position;

            [GhostField(Quantization=100, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
            public float Scale;

            [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
            public quaternion Rotation;
        }

        [Test]
        public void SerializationVariant_AreAppliedToBothRootAndChildEntities()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                var ghostGameObject = new GameObject("Root");
                var childGhost = new GameObject("Child");
                childGhost.transform.parent = ghostGameObject.transform;
                var nestedChildGhost = new GameObject("NestedChild");
                nestedChildGhost.transform.parent = childGhost.transform;
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                var inspection = ghostGameObject.AddComponent<GhostAuthoringInspectionComponent>();
                authoring.DefaultGhostMode = GhostMode.Interpolated;
                authoring.SupportedGhostModes = GhostModeMask.All;

                // 为根实体、子实体和嵌套子实体设置同一 Variant，并验证运行时 Serializer 使用该 Variant
                var attrType = typeof(TransformVariantTest).GetCustomAttribute<GhostComponentVariationAttribute>();
                ulong hash = 0;

                using var collectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostComponentSerializerCollectionData>());
                var collectionData = collectionQuery.GetSingleton<GhostComponentSerializerCollectionData>();
                foreach (var ssIndex in collectionData.SerializationStrategiesComponentTypeMap.GetValuesForKey(attrType.ComponentType))
                {
                    var ss = collectionData.SerializationStrategies[ssIndex];
                    if (ss.DisplayName.ToString().Contains(nameof(TransformVariantTest)))
                    {
                        hash = ss.Hash;
                        goto found;
                    }
                }
                Assert.Fail($"Couldn't find {nameof(TransformVariantTest)} to apply it!");

                found:
                Assert.AreNotEqual(0, hash);
                inspection.ComponentOverrides = new[]
                {
                    new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = typeof(Transforms.LocalTransform).FullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = hash
                    },
                };
                childGhost.AddComponent<NetcodeTransformUsageFlagsTestAuthoring>();
                childGhost.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new[]
                {
                    new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = typeof(Transforms.LocalTransform).FullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = hash
                    },
                };
                nestedChildGhost.AddComponent<NetcodeTransformUsageFlagsTestAuthoring>();
                nestedChildGhost.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new[]
                {
                    new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = typeof(Transforms.LocalTransform).FullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = hash
                    }
                };

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject), "Cannot create ghost collection");
                testWorld.BakeGhostCollection(testWorld.ServerWorld);
                testWorld.BakeGhostCollection(testWorld.ClientWorlds[0]);

                // 注册 Serializer 并完成 System 初始化
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 进入游戏状态，使 Ghost Collection 完成运行时设置
                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.SpawnOnServer(ghostGameObject);

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                var typeIndex = TypeManager.GetTypeIndex<Transforms.LocalTransform>();

                // 检查 Variant 注册与 Prefab 组件索引
                var collection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostSerializerCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(collection);
                // 检查目标 Variant 已注册
                bool variantIsPresent = false;
                foreach (var t in ghostSerializerCollection)
                    variantIsPresent |= t.VariantHash == hash;
                Assert.IsTrue(variantIsPresent);

                var componentIndex = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collection);
                var ghostPrefabCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(collection);
                // 验证 Ghost 上 LocalTransform 对应的组件索引指向该 Variant
                for (int i = 0; i < ghostPrefabCollection[0].NumComponents;++i)
                {
                    var idx = componentIndex[ghostPrefabCollection[0].FirstComponent + i];
                    if (ghostSerializerCollection[idx.SerializerIndex].ComponentType.TypeIndex == typeIndex)
                    {
                        Assert.IsTrue(ghostSerializerCollection[idx.SerializerIndex].VariantHash == hash);
                    }
                }
            }
        }

        [Test]
        public void AddPrefabOverride_InRoot_ComputesGameObjectReference()
        {
            AddPrefabOverride_ComputesGameObjectReference((collection, i) => collection[i]);
        }

        [Test]
        public void AddPrefabOverride_InChild_ComputesGameObjectReference()
        {
            AddPrefabOverride_ComputesGameObjectReference((collection, i) => collection[i].transform.GetChild(0).gameObject);
        }

        [Test]
        public void AddPrefabOverride_InNestedChild_ComputesGameObjectReference()
        {
            AddPrefabOverride_ComputesGameObjectReference((collection, i) => collection[i].transform.GetChild(0).GetChild(0).gameObject);
        }

        private void AddPrefabOverride_ComputesGameObjectReference(Func<GameObject[], int, GameObject> testTransform)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var names = new[] { "All", "Interpolated", "Predicted", "None" };
            var sendTypes = new[]
                { GhostSendType.AllClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.OnlyPredictedClients, GhostSendType.DontSend };
            var collection = CreatePrefabs(names);
            for (int i = 0; i < sendTypes.Length; ++i)
            {
                var goFromFunc = testTransform(collection, i);
                const int exampleEntityIndex = 66;
                var inspection = goFromFunc.GetComponent<GhostAuthoringInspectionComponent>() ?? goFromFunc.AddComponent<GhostAuthoringInspectionComponent>();

                var entityGuid = new EntityGuid
                {
                    a = (ulong)goFromFunc.GetInstanceID(),
                    b = exampleEntityIndex,
                };
                var componentOverride = inspection.GetOrAddPrefabOverride(typeof(GhostGen_IntStruct), entityGuid, (GhostPrefabType) GhostAuthoringInspectionComponent.ComponentOverride.NoOverride);

                var ghostAuthoringComponent = collection[i].GetComponent<GhostAuthoringComponent>();
                Assert.IsNotNull(ghostAuthoringComponent);
                var allComponentOverrides = GhostAuthoringInspectionComponent.CollectAllComponentOverridesInInspectionComponents(ghostAuthoringComponent, false);
                var foundInspection = allComponentOverrides.First(x => x.Item1 == goFromFunc);
                Assert.AreEqual(foundInspection.Item1.GetInstanceID(), entityGuid.OriginatingId, $"entityGuid.OriginatingId '{entityGuid.OriginatingId}' did not match game object set '{goFromFunc}'");
                Assert.AreEqual(foundInspection.Item2.EntityIndex, exampleEntityIndex, "EntityIndex should have been set!");
            }
        }
    }
}
