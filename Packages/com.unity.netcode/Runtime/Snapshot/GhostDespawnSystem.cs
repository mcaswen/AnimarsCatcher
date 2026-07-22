using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>
    /// 仅存在于客户端 World，收到服务器的销毁请求或命令时负责销毁已生成 Ghost
    /// </para>
    /// <para>客户端不负责也不应自行销毁 Ghost 实体
    /// 服务器负责通过 Snapshot 协议通知客户端应销毁哪些 Ghost
    /// </para>
    /// <para>
    /// 收到销毁命令后，Ghost 实体会进入销毁队列
    /// 系统维护两个独立队列，分别用于插值 Ghost 和预测 Ghost
    /// </para>
    /// <para>
    /// 必须区分两者，因为插值 Ghost 的时间线 <see cref="NetworkTime.InterpolationTick"/>
    /// 相对于服务器和客户端当前模拟 Tick 都处于过去
    /// 收到包含插值 Ghost 销毁命令的 Snapshot 时，服务器销毁该实体的 Tick 对此客户端可能仍在未来
    /// 因此客户端必须等待 <see cref="NetworkTime.InterpolationTick"/> 大于或等于销毁 Tick，才能实际销毁 Ghost
    /// </para>
    /// <para>
    /// 预测实体则只能在当前 <see cref="NetworkTime.ServerTick"/> 大于或等于服务器销毁 Tick 时销毁
    /// 因此，如果客户端按预期领先运行，预测 Ghost 的销毁请求一旦从 Snapshot 中取出
    /// 就会在同一帧稍后立即执行销毁
    /// </para>
    /// </summary>
    [BurstCompile]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation)]
    public partial struct GhostDespawnSystem : ISystem
    {
        NativeQueue<DelayedDespawnGhost> m_InterpolatedDespawnQueue;
        NativeQueue<DelayedDespawnGhost> m_PredictedDespawnQueue;

        internal struct DelayedDespawnGhost
        {
            public SpawnedGhost ghost;
            public NetworkTick tick;
        }

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var singleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostDespawnQueues>());
            state.EntityManager.SetName(singleton, "GhostLifetimeComponent-Singleton");
            m_InterpolatedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
            m_PredictedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
            SystemAPI.SetSingleton(new GhostDespawnQueues
            {
                InterpolatedDespawnQueue = m_InterpolatedDespawnQueue,
                PredictedDespawnQueue = m_PredictedDespawnQueue,
            });
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            m_InterpolatedDespawnQueue.Dispose();
            m_PredictedDespawnQueue.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if(!SystemAPI.HasSingleton<NetworkStreamInGame>())
            {
                state.CompleteDependency();
                m_PredictedDespawnQueue.Clear();
                m_InterpolatedDespawnQueue.Clear();
                return;
            }
            if (state.WorldUnmanaged.IsThinClient())
                return;

            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            state.Dependency = new DespawnJob
            {
                spawnedGhostMap = SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRO.SpawnedGhostMapRW,
                interpolatedDespawnQueue = m_InterpolatedDespawnQueue,
                predictedDespawnQueue = m_PredictedDespawnQueue,
                interpolatedTick = networkTime.InterpolationTick,
                predictedTick = networkTime.ServerTick,
                commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged),
            }.Schedule(state.Dependency);
        }

        [BurstCompile]
        struct DespawnJob : IJob
        {
            public NativeQueue<DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeParallelHashMap<SpawnedGhost, Entity> spawnedGhostMap;
            public NativeQueue<DelayedDespawnGhost> predictedDespawnQueue;
            public NetworkTick interpolatedTick, predictedTick;
            public EntityCommandBuffer commandBuffer;

            [BurstCompile]
            public void Execute()
            {
                {
                    while (interpolatedDespawnQueue.Count > 0 &&
                           !interpolatedDespawnQueue.Peek().tick.IsNewerThan(interpolatedTick))
                    {
                        var spawnedGhost = interpolatedDespawnQueue.Dequeue();
                        if (spawnedGhostMap.TryGetValue(spawnedGhost.ghost, out var ent))
                        {
                            commandBuffer.DestroyEntity(ent);
                            spawnedGhostMap.Remove(spawnedGhost.ghost);
                        }
                    }

                    while (predictedDespawnQueue.Count > 0 &&
                           !predictedDespawnQueue.Peek().tick.IsNewerThan(predictedTick))
                    {
                        var spawnedGhost = predictedDespawnQueue.Dequeue();
                        if (spawnedGhostMap.TryGetValue(spawnedGhost.ghost, out var ent))
                        {
                            commandBuffer.DestroyEntity(ent);
                            spawnedGhostMap.Remove(spawnedGhost.ghost);
                        }
                    }
                }
            }
        }
    }
}
