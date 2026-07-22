#if UNITY_6000_0_OR_NEWER
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.LowLevel;

namespace Unity.NetCode.Tests
{
    internal partial class PlayModeTestWorldStrategy : NetCodeTestWorld.ITestWorldStrategy
    {
        NetCodeTestWorld m_TestWorld;
        PlayerLoopSystem m_OldLoop;
        int m_oldThinClientRequestCount;
        public float DeltaTime { private get; set; } = 1 / 60f;
        public World DefaultWorld => World.DefaultGameObjectInjectionWorld;
        public static PlayModeTestWorldStrategy Instance = null;

        public PlayModeTestWorldStrategy()
        {
            Assert.IsNull(Instance, "assumption broken, s_Instance wasn't cleaned up properly");
            Instance = this;
        }

        public void Dispose()
        {
            PlayerLoop.SetPlayerLoop(m_OldLoop);
#if UNITY_EDITOR
            MultiplayerPlayModePreferences.RequestedNumThinClients = m_oldThinClientRequestCount;
#endif
            Instance = null;
        }

        internal void ApplyDT()
        {

        }

        public void Bootstrap(NetCodeTestWorld testWorld)
        {
            this.m_TestWorld = testWorld;

            var mainLoop = PlayerLoop.GetCurrentPlayerLoop();
            var oldLoop = mainLoop;
            Instance.m_OldLoop = oldLoop;
#if UNITY_EDITOR
            m_oldThinClientRequestCount = MultiplayerPlayModePreferences.RequestedNumThinClients;
#endif
            List<PlayerLoopSystem> systemList = new();
            systemList.AddRange(mainLoop.subSystemList);
            systemList.Insert(1, new PlayerLoopSystem()
            {
                type = typeof(PlayModeTestWorldStrategy),
                updateDelegate = UpdateTimeFromUpdateLoop
            });

            mainLoop.subSystemList = systemList.ToArray();

            Assert.AreNotEqual(mainLoop.subSystemList, Instance.m_OldLoop);
            PlayerLoop.SetPlayerLoop(mainLoop);
        }

        #region World 管理
        public World CreateClientWorld(string name, bool thinClient, World world = null)
        {
            if (world == null)
            {
                if (thinClient)
                {
                    TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ThinClientSystems); // 确保遵循 CreationOrder
                    world = ClientServerBootstrap.CreateThinClientWorld(ListToNativeList(NetCodeTestWorld.m_ThinClientSystems));
                }
                else
                {
                    TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ClientSystems); // 确保遵循 CreationOrder
                    world = ClientServerBootstrap.CreateClientWorld(name, ListToNativeList(NetCodeTestWorld.m_ClientSystems));
                }
            }
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
#if UNITY_EDITOR
            if (thinClient)
                MultiplayerPlayModePreferences.RequestedNumThinClients += 1; // 避免代码侧请求与 Editor 设置冲突，导致测试创建的 Thin Client World 被意外销毁
#endif
            return world;
        }

        public World CreateServerWorld(string name, World world = null)
        {
            if (world == null)
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ServerSystems); // 确保遵循 CreationOrder
                world = ClientServerBootstrap.CreateServerWorld(name, ListToNativeList(NetCodeTestWorld.m_ServerSystems));
            }
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
            return world;
        }

        public World CreateHostWorld(string name, World world = null)
        {
            if (world == null)
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_HostSystems); // 确保遵循 CreationOrder
                world = ClientServerBootstrap.CreateSingleWorldHost(name, ListToNativeList(NetCodeTestWorld.m_HostSystems));
            }
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
            return world;
        }
        NativeList<SystemTypeIndex> ListToNativeList(List<Type> list)
        {

            var nativeList = new NativeList<SystemTypeIndex>(list.Count, Allocator.Temp);
            foreach (var type in list)
            {
                nativeList.Add(TypeManager.GetSystemTypeIndex(type));
            }
            return nativeList;
        }

        public void DisposeClientWorld(World world)
        {
            if (m_TestWorld.AlwaysDispose || world.IsCreated)
                world.Dispose();
        }

        public void DisposeServerWorld(World world)
        {
            if (m_TestWorld.AlwaysDispose || world.IsCreated)
                world.Dispose();
        }
        #endregion

        #region Tick 驱动
        public void TickNoAwait(float dt)
        {
            throw new NotSupportedException("Must yield in playmode");
        }

        public async Task TickAsync(float dt, NetcodeAwaitable awaitInstruction = null)
        {
            Instance.DeltaTime = dt;
            if (awaitInstruction == null)
            {
                await Awaitable.NextFrameAsync();
                // await Awaitable.EndOfFrameAsync(); // TODO：该调用在批处理模式下会永久挂起，暂时由需要它的测试自行 yield
            }
            else
                await awaitInstruction;
        }

        public void TickClientWorld(float dt)
        {
            throw new NotImplementedException();
        }

        public void TickServerWorld(float dt)
        {
            throw new NotImplementedException();
        }

        #endregion

        public void RemoveWorldFromUpdateList(World world)
        {
            throw new NotImplementedException();
        }

        public void DisposeDefaultWorld()
        {
            throw new NotImplementedException();
        }

        static void UpdateTimeFromUpdateLoop()
        {
            Instance.m_TestWorld.ApplyDT(Instance.DeltaTime);
        }
    }
}
#endif
