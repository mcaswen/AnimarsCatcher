using System;
using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    internal partial class ExplicitDefaultSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class ExplicitClientSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ExplicitServerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ExplicitClientServerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }

    /// <summary>
    /// <see cref="GhostPredictionHistorySystem"/> 会执行额外的保存与写入，需要覆盖该测试维度
    /// </summary>
    internal enum PredictionSetting
    {
        WithPredictedEntities = 1,
        WithInterpolatedEntities = 2,
    }

    /// <summary>
    /// 定义测试使用的 Variant 及其应用方式，以覆盖全部用户流程
    /// </summary>
    internal enum SendForChildrenTestCase
    {
        /// <summary>
        /// 使用 <see cref="GhostTypeConverter.FetchAllTestComponentTypesRequiringSendRuleOverride"/> 提供的映射
        /// 通过 <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/> 创建父 Entity 与子 Entity Override
        /// </summary>
        YesViaExplicitVariantRule,
        /// <summary>
        /// 通过 <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/> 创建仅子 Entity 使用的 Override
        /// 父 Entity 默认使用 <see cref="DontSerializeVariant"/>
        /// 子 Entity 上的组件仍可能因自身的子 Entity 复制规则而不复制
        /// </summary>
        YesViaExplicitVariantOnlyAllowChildrenToReplicateRule,
        /// <summary>
        /// 通过 <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/> 强制使用 DontSerializeVariant
        /// </summary>
        NoViaExplicitDontSerializeVariantRule,
        /// <summary>
        /// 使用 <see cref="GhostAuthoringInspectionComponent"/> 为子 Entity 定义 Override
        /// </summary>
        YesViaInspectionComponentOverride,
        /// <summary>
        /// 子 Entity 默认使用 <see cref="DontSerializeVariant"/>
        /// 若该类型只有一个 Variant，则默认使用该 Variant
        /// </summary>
        Default,
    }

    [Category(NetcodeTestCategories.Foundational)]
    [Category(NetcodeTestCategories.Smoke)]
    internal class BootstrapTests
    {
        [Test]
        public void BootstrapRespectsUpdateInWorld()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(false,
                    typeof(ExplicitDefaultSystem),
                    typeof(ExplicitClientSystem),
                    typeof(ExplicitServerSystem),
                    typeof(ExplicitClientServerSystem));
                testWorld.CreateWorlds(true, 1);

                Assert.IsNull(testWorld.ServerWorld.GetExistingSystemManaged<ExplicitDefaultSystem>());
                Assert.IsNull(testWorld.ClientWorlds[0].GetExistingSystemManaged<ExplicitDefaultSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystemManaged<ExplicitClientSystem>());
                Assert.IsNull(testWorld.ServerWorld.GetExistingSystemManaged<ExplicitClientSystem>());
                Assert.IsNotNull(testWorld.ClientWorlds[0].GetExistingSystemManaged<ExplicitClientSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystemManaged<ExplicitServerSystem>());
                Assert.IsNotNull(testWorld.ServerWorld.GetExistingSystemManaged<ExplicitServerSystem>());
                Assert.IsNull(testWorld.ClientWorlds[0].GetExistingSystemManaged<ExplicitServerSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystemManaged<ExplicitClientServerSystem>());
                Assert.IsNotNull(testWorld.ServerWorld.GetExistingSystemManaged<ExplicitClientServerSystem>());
                Assert.IsNotNull(testWorld.ClientWorlds[0].GetExistingSystemManaged<ExplicitClientServerSystem>());
            }
        }
        [Test]
        public void DisposingClientServerWorldDoesNotCauseErrors()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(false);
                testWorld.CreateWorlds(true, 1);

                testWorld.Tick();
                testWorld.DisposeAllClientWorlds();
                testWorld.Tick();
                testWorld.DisposeServerWorld();
                testWorld.Tick();
            }
        }
        [Test]
        public void DisposingDefaultWorldBeforeClientServerWorldDoesNotCauseErrors()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(false);
                testWorld.CreateWorlds(true, 1);

                testWorld.Tick();
                testWorld.DisposeDefaultWorld();
            }
        }

        [Test]
        public void ResetNetworkDriverStore()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);

            {
                var serverDriver = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
                var netDebug = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
                // World 状态稳定且没有连接或监听接口时，应允许更换 Driver
                var driverStore = new NetworkDriverStore();
                var constructor = testWorld;
                constructor.CreateServerDriver(testWorld.ServerWorld, ref driverStore, netDebug);
                serverDriver.ResetDriverStore(testWorld.ServerWorld.Unmanaged, ref driverStore);
            }
            {
                var clientDriver = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
                var netDebug = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
                // World 状态稳定且没有连接或监听接口时，应允许更换 Driver
                var driverStore = new NetworkDriverStore();
                var constructor = testWorld;
                constructor.CreateClientDriver(testWorld.ClientWorlds[0], ref driverStore, netDebug);
                clientDriver.ResetDriverStore(testWorld.ClientWorlds[0].Unmanaged, ref driverStore);
            }
            testWorld.Connect();
        }
        [Test]
        public void ResetNetworkDriverStore_ThrowIfConnectionsArePresent()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            {
                var serverDriver = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
                var netDebug = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
                // 已存在连接时，不应允许更换 Driver
                var driverStore = new NetworkDriverStore();
                var constructor = testWorld;
                constructor.CreateServerDriver(testWorld.ServerWorld, ref driverStore, netDebug);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    serverDriver.ResetDriverStore(testWorld.ServerWorld.Unmanaged, ref driverStore);
                });
            }
            {
                var clientDriver = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
                var netDebug = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
                // 已存在连接时，不应允许更换 Driver
                var driverStore = new NetworkDriverStore();
                var constructor = testWorld;
                constructor.CreateClientDriver(testWorld.ClientWorlds[0], ref driverStore, netDebug);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    clientDriver.ResetDriverStore(testWorld.ClientWorlds[0].Unmanaged, ref driverStore);
                });
            }
        }
    }
}
