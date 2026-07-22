using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    [GhostComponentVariation(typeof(HybridComponentWeWillOverride), "Client Only")]
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    internal struct HybridComponentWeWillOverrideVariant
    {
    }
    [DisableAutoCreation]
    partial class HybridComponentWeWillOverrideDefaultVariantSystem : DefaultVariantSystemBase
    {
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            defaultVariants.Add(typeof(HybridComponentWeWillOverride), Rule.ForAll(typeof(HybridComponentWeWillOverrideVariant)));
        }
    }

    internal class HybridComponentWeWillOverrideConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
#if !UNITY_DISABLE_MANAGED_COMPONENTS
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponentObject(entity, gameObject.GetComponent<HybridComponentWeWillOverride>());
#endif
        }
    }
    internal class ServerComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new ServerComponentData {Value = 1});
        }
    }
    internal class ClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new ClientComponentData {Value = 1});
        }
    }
    internal class InterpolatedClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new InterpolatedClientComponentData {Value = 1});
        }
    }
    internal class PredictedClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new PredictedClientComponentData {Value = 1});
        }
    }
    internal class AllPredictedComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new AllPredictedComponentData {Value = 1});
        }
    }
    internal class AllComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new AllComponentData {Value = 1});
        }
    }
    internal class ServerHybridComponentConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
#if !UNITY_DISABLE_MANAGED_COMPONENTS
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponentObject(entity, gameObject.GetComponent<ServerHybridComponent>());
#endif
        }
    }
    internal class ClientHybridComponentConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
#if !UNITY_DISABLE_MANAGED_COMPONENTS
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponentObject(entity, gameObject.GetComponent<ClientHybridComponent>());
#endif
        }
    }

    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public class HybridComponentWeWillOverride : MonoBehaviour
    {
        public int value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    internal struct ServerComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    internal struct ClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    internal class ClientHybridComponent : MonoBehaviour
    {
        public int value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    internal class ServerHybridComponent : MonoBehaviour
    {
        public int value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient)]
    internal struct InterpolatedClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.PredictedClient)]
    internal struct PredictedClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    internal struct AllPredictedComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.All)]
    internal struct AllComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }

    [Category(NetcodeTestCategories.Foundational)]
    internal class GameObjectConversionTest
    {
        void CheckComponent(World w, ComponentType testType, int expectedCount)
        {
            using var query = w.EntityManager.CreateEntityQuery(testType);
            using (var ghosts = query.ToEntityArray(Allocator.Temp))
            {
                var compCount = ghosts.Length;
                Assert.AreEqual(expectedCount, compCount);
            }
        }


        [Test]
        public void ComponentsStrippedAccordingToGhostConfig()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(HybridComponentWeWillOverrideDefaultVariantSystem));

                var gameObject0 = new GameObject();
                // 支持全部 Ghost 模式，默认使用插值模式
                var ghostComponent = gameObject0.AddComponent<GhostAuthoringComponent>();
                ghostComponent.SupportedGhostModes = GhostModeMask.All;
                gameObject0.AddComponent<HybridComponentWeWillOverride>();
                gameObject0.AddComponent<ClientHybridComponent>();
                gameObject0.AddComponent<ServerHybridComponent>();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new HybridComponentWeWillOverrideConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ServerHybridComponentConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ClientHybridComponentConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ServerComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ClientComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new InterpolatedClientComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedClientComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new AllPredictedComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new AllComponentDataConverter();
                gameObject0.name = "TestConversionGOAll";

                var gameObject1 = new GameObject();
                // 只支持预测模式，默认模式配置为插值模式
                ghostComponent = gameObject1.AddComponent<GhostAuthoringComponent>();
                ghostComponent.SupportedGhostModes = GhostModeMask.Predicted;
                gameObject1.AddComponent<ClientHybridComponent>();
                gameObject1.AddComponent<ServerHybridComponent>();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new ServerHybridComponentConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new ClientHybridComponentConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new ServerComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new ClientComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new InterpolatedClientComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedClientComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new AllPredictedComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new AllComponentDataConverter();
                gameObject1.name = "TestConversionGOPredicted";

                var gameObject2 = new GameObject();
                // 只支持插值模式，默认使用插值模式
                ghostComponent = gameObject2.AddComponent<GhostAuthoringComponent>();
                ghostComponent.SupportedGhostModes = GhostModeMask.Interpolated;
                gameObject2.AddComponent<ClientHybridComponent>();
                gameObject2.AddComponent<ServerHybridComponent>();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new ServerHybridComponentConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new ClientHybridComponentConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new ServerComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new ClientComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new InterpolatedClientComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedClientComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new AllPredictedComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new AllComponentDataConverter();
                gameObject2.name = "TestConversionGOInterpolated";

                Assert.IsTrue(testWorld.CreateGhostCollection(
                    gameObject0, gameObject1, gameObject2));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(gameObject0);
                testWorld.SpawnOnServer(gameObject1);
                testWorld.SpawnOnServer(gameObject2);

#if !UNITY_DISABLE_MANAGED_COMPONENTS
                // HybridComponent 原本配置为服务端组件，但 Variant 覆盖规则将其改为仅客户端组件
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<HybridComponentWeWillOverride>(), 0);
#endif

                // 服务端不会保留客户端类型的 Ghost 组件
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ClientComponentData>(), 0);
#if !UNITY_DISABLE_MANAGED_COMPONENTS
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ClientHybridComponent>(), 0);
#endif
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InterpolatedClientComponentData>(), 0);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<PredictedClientComponentData>(), 0);

                // 服务端始终保留 All 和 Server 类型的 Ghost 组件
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ServerComponentData>(), 3);
#if !UNITY_DISABLE_MANAGED_COMPONENTS
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ServerHybridComponent>(), 3);
#endif
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<AllComponentData>(), 3);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<AllPredictedComponentData>(), 3);

                testWorld.Connect();
                testWorld.GoInGame();
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

#if !UNITY_DISABLE_MANAGED_COMPONENTS
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<HybridComponentWeWillOverride>(), 1);
#endif

                // 客户端 Ghost 不会保留服务端类型的组件
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ServerComponentData>(), 0);
#if !UNITY_DISABLE_MANAGED_COMPONENTS
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ServerHybridComponent>(), 0);
#endif

                // 客户端中，支持预测模式的 Ghost 会获得预测组件，而支持全部模式的 Ghost 默认仍为插值模式
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<PredictedClientComponentData>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<AllPredictedComponentData>(), 1);

                // 客户端中，支持全部模式或插值模式的 Ghost 会获得插值组件
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InterpolatedClientComponentData>(), 2);

                // 所有客户端 Ghost 都会获得其余客户端通用组件
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ClientComponentData>(), 3);
#if !UNITY_DISABLE_MANAGED_COMPONENTS
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ClientHybridComponent>(), 3);
#endif
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<AllComponentData>(), 3);
            }
        }
    }
}
