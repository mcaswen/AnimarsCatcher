using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine;

namespace Unity.NetCode.Tests
{

    internal class EditModeTestWorldStrategy : NetCodeTestWorld.ITestWorldStrategy
    {
        NetCodeTestWorld m_TestWorld;
        private bool m_DefaultWorldInitialized;
        internal List<World> m_WorldsToUpdate;
        private World m_DefaultWorld;

        static readonly ProfilerMarker k_TickServerInitializationSystem = new ProfilerMarker("TickServerInitializationSystem");
        static readonly ProfilerMarker k_TickClientInitializationSystem = new ProfilerMarker("TickClientInitializationSystem");
        static readonly ProfilerMarker k_TickServerSimulationSystem = new ProfilerMarker("TickServerSimulationSystem");
        static readonly ProfilerMarker k_TickClientSimulationSystem = new ProfilerMarker("TickClientSimulationSystem");
        static readonly ProfilerMarker k_TickClientPresentationSystem = new ProfilerMarker("TickClientPresentationSystem");

        public World CreateServerWorld(string name, World world = null)
        {
            EnsureDefaultWorldInitialized();
            if (world == null)
                world = new World(name, WorldFlags.GameServer);
            ClientServerBootstrap.AssignCurrentActiveWorldIfNotSet(world);
            TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ServerSystems); // 确保遵循 CreationOrder
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, NetCodeTestWorld.m_ServerSystems);
#if !UNITY_CLIENT || UNITY_EDITOR
            AppendWorldToUpdateList(world);
#endif
            if (!m_TestWorld.m_IncludeNetcodeSystems)
            {
                // 该注册通常由 ConfigureServerWorldSystem.OnCreate 完成，仅在未包含 NetCode 系统时手动补齐
                ClientServerBootstrap.ServerWorlds.Add(world);
            }
#if UNITY_EDITOR
            m_TestWorld.BakeGhostCollection(world);
#endif
            return world;
        }

        public World CreateHostWorld(string name, World world = null)
        {
            EnsureDefaultWorldInitialized();
            if (world == null)
                world = new World(name, WorldFlags.GameServer | WorldFlags.GameClient);
            ClientServerBootstrap.AssignCurrentActiveWorldIfNotSet(world);

            TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_HostSystems); // 确保遵循 CreationOrder
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, NetCodeTestWorld.m_HostSystems);
#if !UNITY_SERVER || UNITY_EDITOR
            AppendWorldToUpdateList(world);
#endif
            if (!m_TestWorld.m_IncludeNetcodeSystems)
            {
                // 该注册通常由 ConfigureClientWorldSystem.OnCreate 完成，仅在未包含 NetCode 系统时手动补齐
                ClientServerBootstrap.ClientWorlds.Add(world);
                ClientServerBootstrap.ServerWorlds.Add(world);
            }
#if UNITY_EDITOR
            m_TestWorld.BakeGhostCollection(world);
#endif
            return world;
        }

        public void Bootstrap(NetCodeTestWorld testWorld)
        {
            this.m_TestWorld = testWorld;
            m_WorldsToUpdate = new List<World>();
            m_DefaultWorld = new World("NetCodeTest");
        }

        public void Dispose()
        {
            if (m_DefaultWorld != null)
                m_DefaultWorld.Dispose();
            m_DefaultWorld = null;
        }

        public void DisposeDefaultWorld()
        {
            m_DefaultWorld.Dispose();
            m_DefaultWorld = null;
        }

        public World CreateClientWorld(string name, bool thinClient, World world = null)
        {
            EnsureDefaultWorldInitialized();
            if (world == null)
                world = new World(name, thinClient ? WorldFlags.GameThinClient : WorldFlags.GameClient);
            ClientServerBootstrap.AssignCurrentActiveWorldIfNotSet(world);
            if (world.IsThinClient())
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ThinClientSystems); // 确保遵循 CreationOrder
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, NetCodeTestWorld.m_ThinClientSystems);
            }
            else
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ClientSystems); // 确保遵循 CreationOrder
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, NetCodeTestWorld.m_ClientSystems);
            }
#if !UNITY_SERVER || UNITY_EDITOR
            AppendWorldToUpdateList(world);
#endif
            if (!m_TestWorld.m_IncludeNetcodeSystems)
            {
                // 该注册通常由 ConfigureClientWorldSystem.OnCreate 完成，仅在未包含 NetCode 系统时手动补齐
                ClientServerBootstrap.ClientWorlds.Add(world);
            }
#if UNITY_EDITOR
            m_TestWorld.BakeGhostCollection(world);
#endif
            return world;
        }


        void EnsureDefaultWorldInitialized()
        {
            if (!m_DefaultWorldInitialized)
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ControlSystems); // 确保遵循 CreationOrder
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(m_DefaultWorld,
                    NetCodeTestWorld.m_ControlSystems);
                m_DefaultWorldInitialized = true;
            }
        }

        public void AppendWorldToUpdateList(World world)
        {
            var tracker = world.GetOrCreateSystemManaged<NetCodeTestWorldWorldTracker>();
            tracker.testWorld = m_TestWorld;
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
            //world.GetExistingSystemManaged<SimulationSystemGroup>().RemoveSystemFromUpdateList(world.GetExistingSystemManaged<EarlyUpdateSystemGroup>());
            //world.GetExistingSystemManaged<SimulationSystemGroup>().RemoveSystemFromUpdateList(world.GetExistingSystemManaged<PreLateUpdateSystemGroup>());
            m_WorldsToUpdate.Add(world);
        }

        public void DisposeClientWorld(World clientWorld)
        {
            if (clientWorld != null)
            {
                RemoveWorldFromUpdateList(clientWorld);
                if (!m_TestWorld.m_IncludeNetcodeSystems)
                {
                    ClientServerBootstrap.ClientWorlds.Remove(clientWorld);
                }
                if (m_TestWorld.AlwaysDispose || clientWorld.IsCreated) // 关闭测试可能已销毁 World，无需再次 Dispose
                {
                    clientWorld.Dispose();
                }
            }
        }

        public void DisposeServerWorld(World serverWorld)
        {
            if (serverWorld != null)
            {
                RemoveWorldFromUpdateList(serverWorld);
                if (!m_TestWorld.m_IncludeNetcodeSystems) ClientServerBootstrap.ServerWorlds.Remove(serverWorld);
                if (m_TestWorld.AlwaysDispose || serverWorld.IsCreated)
                {
                    serverWorld.Dispose();
                }
            }
        }

        // 此处不能直接调用 TickClientWorld 或 TickServerWorld，因为必须按正确顺序交错更新各 World
        public void TickNoAwait(float dt)
        {
            m_TestWorld.ApplyDT(dt);

            // 逐个更新初始化组，避免由完整 PlayerLoop 隐式驱动日志刷新
            foreach (var world in m_WorldsToUpdate)
            {
                var marker = world.IsClient()
                    ? k_TickClientInitializationSystem
                    : k_TickServerInitializationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(InitializationSystemGroup));
            }

            FlushLogs();
            // TODO: 添加正确的 ProfilerMarker
            foreach (var world in m_WorldsToUpdate)
            {
                var marker = world.IsClient()
                    ? k_TickClientInitializationSystem
                    : k_TickServerInitializationSystem;
                //UpdateWorldSystemGroup(world, ref marker, typeof(EarlyUpdateSystemGroup));
            }

            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                var marker = world.IsClient()
                    ? k_TickClientSimulationSystem
                    : k_TickServerSimulationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(SimulationSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                var profilerMarker = k_TickClientPresentationSystem;
                //UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PreLateUpdateSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsClient())
                    continue;
                var profilerMarker = k_TickClientPresentationSystem;
                UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PresentationSystemGroup));
            }
            FlushLogs();
#if USING_UNITY_LOGGING
            // 主动刷新待处理日志，因为负责刷新的系统可能尚未运行，否则 Log.Expect 无法生效
            Logging.Internal.LoggerManager.ScheduleUpdateLoggers().Complete();
#endif
        }

#pragma warning disable CS1998 // 异步方法不含 await，将同步运行
        public async Task TickAsync(float dt, NetcodeAwaitable waitInstruction = null)
#pragma warning restore CS1998 // 异步方法不含 await，将同步运行
        {
            // NetCodeTestWorld 的多种 Tick 方法都会调用此方法，例如 TickUntilConnected
            // 为复用同一套实现，这些方法统一调用 TickAsync，并在 Edit Mode 中同步等待
            // 因此 Edit Mode 下不执行异步等待，只直接调用 TickNoAwait
            Assert.IsNull(waitInstruction, "Awaitable not supported in edit mode");
            TickNoAwait(dt);
        }

        public void TickClientWorld(float dt)
        {
            m_TestWorld.ApplyDTClient(dt);

            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsClient())
                    continue;
                var marker = k_TickClientInitializationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(InitializationSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsClient())
                    continue;
                var marker = k_TickClientInitializationSystem;
                //UpdateWorldSystemGroup(world, ref marker, typeof(EarlyUpdateSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsClient())
                    continue;
                var marker = k_TickClientSimulationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(SimulationSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsClient())
                    continue;
                var profilerMarker = k_TickClientPresentationSystem;
                //UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PreLateUpdateSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsClient())
                    continue;
                var profilerMarker = k_TickClientPresentationSystem;
                UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PresentationSystemGroup));
            }
            FlushLogs();
        }

        public void TickServerWorld(float dt)
        {
            m_TestWorld.ApplyDTServer(dt);

            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsServer())
                    continue;
                var marker = k_TickServerInitializationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(InitializationSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsServer())
                    continue;
                var marker = k_TickServerInitializationSystem;
                //UpdateWorldSystemGroup(world, ref marker, typeof(EarlyUpdateSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsServer())
                    continue;
                var marker = k_TickServerSimulationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(SimulationSystemGroup));
            }
            FlushLogs();
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsServer())
                    continue;
                var profilerMarker = k_TickClientPresentationSystem;
                //UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PreLateUpdateSystemGroup));
            }
            FlushLogs();
        }

        public void FlushLogs()
        {
#if USING_UNITY_LOGGING
            Logging.Internal.LoggerManager.ScheduleUpdateLoggers().Complete();
#endif
        }

        public void RemoveWorldFromUpdateList(World world)
        {
            m_WorldsToUpdate.Remove(world);
        }

        public World DefaultWorld => m_DefaultWorld;

        private static void UpdateWorldSystemGroup(World world, ref ProfilerMarker marker, Type systemType)
        {
            marker.Begin();
            var systemManaged = world.GetExistingSystemManaged(systemType);
            systemManaged?.Update();
            marker.End();
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    internal partial class NetCodeTestWorldWorldTracker : SystemBase
    {
        public NetCodeTestWorld testWorld;

        protected override void OnCreate()
        {
            Enabled = false;
        }
        protected override void OnDestroy()
        {
            testWorld.RemoveWorldFromUpdateList(World);
        }
        protected override void OnUpdate()
        {
            throw new NotImplementedException();
        }
    }

}
