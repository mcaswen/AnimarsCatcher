using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Transforms;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal class GhostPrefabCreationTests
    {
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void CreateGhostPrefabWithChildren()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            CreatePrefab(testWorld.ServerWorld.EntityManager);
            CreatePrefab(testWorld.ClientWorlds[0].EntityManager);
            testWorld.Connect();
            testWorld.GoInGame();
            // 将 Prefab 注册到 Ghost Collection System
            testWorld.Tick();
            var serverCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
            var components = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentType>(serverCollection);
            var types = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(serverCollection);
            var indices = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(serverCollection);
            var serializers = testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(serverCollection);
            var typeData = types[0];
            // 根实体上的所有组件都应参与序列化
            for (int i = 0; i < typeData.NumComponents - typeData.NumChildComponents; ++i)
            {
                Assert.AreNotEqual(GhostVariantsUtility.DontSerializeHash, serializers[indices[i + typeData.FirstComponent].SerializerIndex].VariantHash);
            }
            for (int i = typeData.NumComponents - typeData.NumChildComponents; i < typeData.NumComponents; ++i)
            {
                var compIdx = indices[i + typeData.FirstComponent].ComponentIndex;
                if (components[compIdx].Type == ComponentType.ReadWrite<EnableableComponent_0>() ||
                    components[compIdx].Type == ComponentType.ReadWrite<EnableableBuffer_0>())
                {
                    Assert.AreNotEqual(GhostVariantsUtility.DontSerializeHash, serializers[indices[i + typeData.FirstComponent].SerializerIndex].VariantHash);
                }
                else
                {
                    Assert.AreEqual(GhostVariantsUtility.DontSerializeHash, serializers[indices[i + typeData.FirstComponent].SerializerIndex].VariantHash);
                }
            }
        }

        [Test]
        public void CreateGhostPrefab_UseCustomGhostType([Values]bool useValidGuid)
        {
            static void CreateGhost(EntityManager entityManager, Hash128 guid)
            {
                var prefab = entityManager.CreateEntity();
                entityManager.AddComponentData(prefab, new EnableableComponent_0());
                entityManager.AddComponentData(prefab, new EnableableComponent_1());
                entityManager.AddComponentData(prefab, new EnableableComponent_2());
                entityManager.AddComponentData(prefab, new EnableableComponent_3());
                entityManager.AddComponentData(prefab, LocalTransform.Identity);
                // 名称只用于测试，不承载业务语义
                GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
                {
                    Name = "TestPrefab",
                    UUID5GhostType = guid,
                    Importance = 0,
                    SupportedGhostModes = GhostModeMask.All,
                    DefaultGhostMode = GhostMode.Interpolated,
                    OptimizationMode = GhostOptimizationMode.Dynamic,
                    UsePreSerialization = false
                });
            }
            // 使用一个随机 Hash 作为测试输入
            var uuid5 = new Hash128("fab209a2a8812a72bffa3198aebaba9f");
            // 转换为规范的 UUID5，API 本身不会强制这一点，但测试需要区分两种输入
            if(useValidGuid)
                uuid5 = GhostPrefabCreation.ConvertHash128ToUUID5(uuid5);
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            if (!useValidGuid)
            {
                Assert.Throws<InvalidOperationException>(() => CreateGhost(testWorld.ServerWorld.EntityManager, uuid5));
                Assert.Throws<InvalidOperationException>(() => CreateGhost(testWorld.ClientWorlds[0].EntityManager, uuid5));
                return;
            }
            Assert.DoesNotThrow(() => CreateGhost(testWorld.ServerWorld.EntityManager, uuid5));
            Assert.DoesNotThrow(() => CreateGhost(testWorld.ClientWorlds[0].EntityManager, uuid5));
            testWorld.Connect();
            testWorld.GoInGame();
            // 将 Prefab 注册到 Ghost Collection System
            testWorld.Tick();
            var serverCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
            var types = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefab>(serverCollection);
            var typeData = types[0];
            Assert.AreEqual(uuid5, (Hash128)typeData.GhostType);
        }

        private static Entity CreatePrefab(EntityManager entityManager)
        {
            // 创建一个包含根实体和单个子实体的 Ghost
            // 根实体与子实体的组件集合不同，因此会使用不同的 Archetype
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, new EnableableComponent_0());
            entityManager.AddComponentData(prefab, new EnableableComponent_1());
            entityManager.AddComponentData(prefab, new EnableableComponent_2());
            entityManager.AddComponentData(prefab, new EnableableComponent_3());
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<GhostOwner>(prefab);
            entityManager.AddBuffer<EnableableBuffer_0>(prefab);
            entityManager.AddBuffer<EnableableBuffer_1>(prefab);
            entityManager.AddBuffer<EnableableBuffer_2>(prefab);
            var child = entityManager.CreateEntity();
            var linkedEntityGroups = entityManager.AddBuffer<LinkedEntityGroup>(prefab);
            linkedEntityGroups.Add(prefab);
            linkedEntityGroups.Add(child);
            entityManager.AddComponent<Prefab>(child);
            entityManager.AddComponentData(child, new EnableableComponent_0());
            entityManager.AddComponentData(child, new EnableableComponent_1());
            entityManager.AddBuffer<EnableableBuffer_0>(child);
            entityManager.AddBuffer<EnableableBuffer_1>(child);


            // 通过覆盖规则设置 Variant，使 EnableableComponent_0 和 EnableableBuffer_0 参与复制
            // EnableableComponent_1 到 EnableableComponent_3 以及 EnableableBuffer_1 不应参与复制
            var overrides = SetupComponentOverrides();

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "TestPrefab",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            },overrides);

            return prefab;
        }

        private static NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride> SetupComponentOverrides()
        {
            var overrides = new NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride>(16, Allocator.Temp);
            overrides.Add(
                new GhostPrefabCreation.Component
                {
                    ComponentType = ComponentType.ReadOnly<EnableableComponent_0>(),
                    ChildIndex = 1
                },
                new GhostPrefabCreation.ComponentOverride
                {
                    OverrideType = GhostPrefabCreation.ComponentOverrideType.Variant,
                    Variant = GhostVariantsUtility.CalculateVariantHashForComponent(ComponentType.ReadOnly<EnableableComponent_0>())
                });
            overrides.Add(
                new GhostPrefabCreation.Component
                {
                    ComponentType = ComponentType.ReadOnly<EnableableBuffer_0>(),
                    ChildIndex = 1
                },
                new GhostPrefabCreation.ComponentOverride
                {
                    OverrideType = GhostPrefabCreation.ComponentOverrideType.Variant,
                    Variant = GhostVariantsUtility.CalculateVariantHashForComponent(ComponentType.ReadOnly<EnableableBuffer_0>())
                });
            return overrides;
        }
    }
}
