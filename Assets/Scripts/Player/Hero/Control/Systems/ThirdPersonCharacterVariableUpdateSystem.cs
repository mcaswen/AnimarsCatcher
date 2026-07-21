namespace AnimarsCatcher.Player
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.Collections;
    using Unity.Jobs;
    using Unity.Mathematics;
    using Unity.Physics;
    using Unity.Transforms;
    using Unity.CharacterController;
    using Unity.Burst.Intrinsics;
    using Unity.NetCode;


    /// <summary>
    /// 在预测组中执行 KCC 可变帧率姿态更新
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [BurstCompile]
    public partial struct ThirdPersonCharacterVariableUpdateSystem : ISystem
    {
        private EntityQuery _characterQuery;
        private ThirdPersonCharacterUpdateContext _context;
        private KinematicCharacterUpdateContext _baseContext;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _characterQuery = KinematicCharacterUtilities.GetBaseCharacterQueryBuilder()
                .WithAll<
                    ThirdPersonCharacter,
                    ThirdPersonCharacterControl>()
                .Build(ref state);

            _context = new ThirdPersonCharacterUpdateContext();
            _context.OnSystemCreate(ref state);
            _baseContext = new KinematicCharacterUpdateContext();
            _baseContext.OnSystemCreate(ref state);

            state.RequireForUpdate(_characterQuery);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _context.OnSystemUpdate(ref state);
            _baseContext.OnSystemUpdate(ref state, SystemAPI.Time, SystemAPI.GetSingleton<PhysicsWorldSingleton>());

            if (SystemAPI.TryGetSingleton<NetworkTime>(out var netTime))
            {
                _context.DebugTick = netTime.ServerTick.SerializedData;
            }

            ThirdPersonCharacterVariableUpdateJob job = new ThirdPersonCharacterVariableUpdateJob
            {
                Context = _context,
                BaseContext = _baseContext,
            };
            job.ScheduleParallel();
        }

        /// <summary>
        /// 以 Chunk 为单位复用 KCC 临时集合并执行可变更新
        /// </summary>
        [BurstCompile]
        [WithAll(typeof(Simulate))]
        public partial struct ThirdPersonCharacterVariableUpdateJob : IJobEntity, IJobEntityChunkBeginEnd
        {
            public ThirdPersonCharacterUpdateContext Context;
            public KinematicCharacterUpdateContext BaseContext;

            void Execute(ThirdPersonCharacterAspect characterAspect)
            {
                characterAspect.VariableUpdate(ref Context, ref BaseContext);
            }

            /// <summary>
            /// 在处理 Chunk 前确保 KCC 临时集合已创建
            /// </summary>
            /// <param name="chunk">当前实体块</param>
            /// <param name="unfilteredChunkIndex">未过滤实体块索引</param>
            /// <param name="useEnabledMask">是否使用启用掩码</param>
            /// <param name="chunkEnabledMask">实体块启用掩码</param>
            /// <returns>是否继续执行当前实体块</returns>
            public bool OnChunkBegin(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                BaseContext.EnsureCreationOfTmpCollections();
                return true;
            }

            /// <summary>
            /// 完成当前实体块的可变姿态更新
            /// </summary>
            /// <param name="chunk">当前实体块</param>
            /// <param name="unfilteredChunkIndex">未过滤实体块索引</param>
            /// <param name="useEnabledMask">是否使用启用掩码</param>
            /// <param name="chunkEnabledMask">实体块启用掩码</param>
            /// <param name="chunkWasExecuted">实体块是否已执行</param>
            public void OnChunkEnd(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask, bool chunkWasExecuted)
            { }
        }
    }
}
