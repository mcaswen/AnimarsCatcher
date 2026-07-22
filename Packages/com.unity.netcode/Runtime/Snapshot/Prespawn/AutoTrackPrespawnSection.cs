#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;
using Unity.Burst;

namespace Unity.NetCode
{
    /// <summary>
    /// 控制 Prespawn 流式传输的 RPC，在场景加载时由客户端发送给服务器
    /// 服务器收到后会为启用流式传输的场景发送其中预生成 Ghost 的新 Snapshot 更新
    /// </summary>
    internal struct StartStreamingSceneGhosts : IRpcCommand
    {
        /// <summary>
        /// 每个包含预生成 Ghost 的 SubScene 所对应的确定性唯一 Hash
        /// 参见 <see cref="SubSceneWithPrespawnGhosts"/>
        /// </summary>
        public ulong SceneHash;
    }

    /// <summary>
    /// 控制 Prespawn 流式传输的 RPC，在场景卸载时由客户端发送给服务器
    /// 服务器收到后不再发送已禁用流式传输场景中预生成 Ghost 的 Snapshot 更新
    /// </summary>
    internal struct StopStreamingSceneGhosts : IRpcCommand
    {
        /// <summary>
        /// 每个包含预生成 Ghost 的 SubScene 所对应的确定性唯一 Hash
        /// 参见 <see cref="SubSceneWithPrespawnGhosts"/>
        /// </summary>
        public ulong SceneHash;
    }

    /// <summary>
    /// 跟踪 Prespawn Section 的加载与卸载事件，并向服务器发送 RPC 上报该客户端已加载的场景
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateBefore(typeof(ClientTrackLoadedPrespawnSections))]
    [BurstCompile]
    partial struct ClientPrespawnAckSystem : ISystem
    {
        ComponentLookup<IsSectionLoaded> m_SectionLoadedFromEntity;
        private EntityQuery m_InitializedSections;
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkId>()
                .WithNone<NetworkStreamRequestDisconnect>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
            m_InitializedSections = state.GetEntityQuery(ComponentType.ReadOnly<SubSceneWithGhostCleanup>());
            state.RequireForUpdate(m_InitializedSections);

            m_SectionLoadedFromEntity = state.GetComponentLookup<IsSectionLoaded>(true);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<DisableAutomaticPrespawnSectionReporting>())
            {
                state.Enabled = false;
                return;
            }

            m_SectionLoadedFromEntity.Update(ref state);
            var ackJob = new ClientPrespawnAck
            {
                sectionLoadedFromEntity = m_SectionLoadedFromEntity,
                netDebug = SystemAPI.GetSingleton<NetDebug>(),
                entityCommandBuffer = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged)
            };
            state.Dependency = ackJob.Schedule(state.Dependency);
        }
        [BurstCompile]
        partial struct ClientPrespawnAck : IJobEntity
        {
            [ReadOnly] public ComponentLookup<IsSectionLoaded> sectionLoadedFromEntity;
            public NetDebug netDebug;
            public EntityCommandBuffer entityCommandBuffer;
            public void Execute(Entity entity, ref SubSceneWithGhostCleanup stateComponent)
            {
                bool isLoaded = sectionLoadedFromEntity.HasComponent(entity);
                if (!isLoaded && stateComponent.Streaming != 0)
                {
                    var reqUnload = entityCommandBuffer.CreateEntity();
                    entityCommandBuffer.AddComponent(reqUnload, new StopStreamingSceneGhosts
                    {
                        SceneHash = stateComponent.SubSceneHash,
                    });
                    entityCommandBuffer.AddComponent(reqUnload, new SendRpcCommandRequest());
                    stateComponent.Streaming = 0;
                    LogStopStreaming(netDebug, stateComponent);
                }
                else if (isLoaded && stateComponent.Streaming == 0)
                {
                    var reqUnload = entityCommandBuffer.CreateEntity();
                    entityCommandBuffer.AddComponent(reqUnload, new StartStreamingSceneGhosts
                    {
                        SceneHash = stateComponent.SubSceneHash
                    });
                    entityCommandBuffer.AddComponent(reqUnload, new SendRpcCommandRequest());
                    stateComponent.Streaming = 1;
                    LogStartStreaming(netDebug, stateComponent);
                }
            }
        }

        [Conditional("NETCODE_DEBUG")]
        private static void LogStopStreaming(in NetDebug netDebug, in SubSceneWithGhostCleanup stateComponent)
        {
            netDebug.DebugLog(FixedString.Format("Request stop streaming scene {0}",
                NetDebug.PrintHex(stateComponent.SubSceneHash)));
        }
        [Conditional("NETCODE_DEBUG")]
        private static void LogStartStreaming(in NetDebug netDebug, in SubSceneWithGhostCleanup stateComponent)
        {
            netDebug.DebugLog(FixedString.Format("Request start streaming scene {0}",
                NetDebug.PrintHex(stateComponent.SubSceneHash)));
        }
    }

    /// <summary>
    /// 处理客户端发来的 StartStreaming 与 StopStreaming RPC，并更新正在流式传输或已确认的场景列表
    /// 可以在该 System 运行前消费或读取 RPC，以添加用户自定义行为
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateBefore(typeof(ServerTrackLoadedPrespawnSections))]
    [BurstCompile]
    partial struct ServerPrespawnAckSystem : ISystem
    {
        BufferLookup<PrespawnSectionAck> m_PrespawnSectionAckFromEntity;
        ComponentLookup<NetworkId> m_NetworkIdLookup;
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_PrespawnSectionAckFromEntity = state.GetBufferLookup<PrespawnSectionAck>();
            m_NetworkIdLookup = state.GetComponentLookup<NetworkId>(true);
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ReceiveRpcCommandRequest>()
                .WithAny<StartStreamingSceneGhosts, StopStreamingSceneGhosts>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<DisableAutomaticPrespawnSectionReporting>())
            {
                state.Enabled = false;
                return;
            }
            m_PrespawnSectionAckFromEntity.Update(ref state);
            m_NetworkIdLookup.Update(ref state);
            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            var startJob = new StartStreamingScene
            {
                prespawnSectionAckFromEntity = m_PrespawnSectionAckFromEntity,
                networkIdLookup = m_NetworkIdLookup,
                ecb = ecb,
                netDebug = netDebug
            };
            state.Dependency = startJob.Schedule(state.Dependency);
            var stopJob = new StopStreamingScene
            {
                prespawnSectionAckFromEntity = m_PrespawnSectionAckFromEntity,
                networkIdLookup = m_NetworkIdLookup,
                ecb = ecb,
                netDebug = netDebug
            };
            state.Dependency = stopJob.Schedule(state.Dependency);
        }
        [BurstCompile]
        partial struct StartStreamingScene : IJobEntity
        {
            public BufferLookup<PrespawnSectionAck> prespawnSectionAckFromEntity;
            [ReadOnly] public ComponentLookup<NetworkId> networkIdLookup;
            public EntityCommandBuffer ecb;
            public NetDebug netDebug;
            public void Execute(Entity entity, in StartStreamingSceneGhosts streamingReq, in ReceiveRpcCommandRequest requestComponent)
            {
                var prespawnSceneAcks = prespawnSectionAckFromEntity[requestComponent.SourceConnection];
                int ackIdx = prespawnSceneAcks.IndexOf(streamingReq.SceneHash);
                if (ackIdx == -1)
                {
                    LogStartStreaming(netDebug, networkIdLookup[requestComponent.SourceConnection].Value, streamingReq.SceneHash);
                    prespawnSceneAcks.Add(new PrespawnSectionAck { SceneHash = streamingReq.SceneHash });
                }
                ecb.DestroyEntity(entity);
            }
        }
        [BurstCompile]
        partial struct StopStreamingScene : IJobEntity
        {
            public BufferLookup<PrespawnSectionAck> prespawnSectionAckFromEntity;
            [ReadOnly] public ComponentLookup<NetworkId> networkIdLookup;
            public EntityCommandBuffer ecb;
            public NetDebug netDebug;
            public void Execute(Entity entity, in StopStreamingSceneGhosts streamingReq, in ReceiveRpcCommandRequest requestComponent)
            {
                var prespawnSceneAcks = prespawnSectionAckFromEntity[requestComponent.SourceConnection];
                int ackIdx = prespawnSceneAcks.IndexOf(streamingReq.SceneHash);
                if (ackIdx != -1)
                {
                    LogStopStreaming(netDebug, networkIdLookup[requestComponent.SourceConnection].Value, streamingReq.SceneHash);
                    prespawnSceneAcks.RemoveAtSwapBack(ackIdx);
                }
                ecb.DestroyEntity(entity);
            }
        }

        [Conditional("NETCODE_DEBUG")]
        private static void LogStopStreaming(in NetDebug netDebug, int connection, ulong sceneHash)
        {
            netDebug.DebugLog(FixedString.Format("Connection {0} stop streaming scene {1}", connection, NetDebug.PrintHex(sceneHash)));
        }
        [Conditional("NETCODE_DEBUG")]
        private static void LogStartStreaming(in NetDebug netDebug, int connection, ulong sceneHash)
        {
            netDebug.DebugLog(FixedString.Format("Connection {0} start streaming scene {1}", connection, NetDebug.PrintHex(sceneHash)));
        }
    }
}
