using Unity.Assertions;
using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;

namespace Unity.NetCode
{
    /// <summary>
    /// Ghost Entity 在预测与插值模式间切换时临时添加的结构体
    /// 由 <see cref="GhostPredictionSwitchingSystem"/> 在处理 <see cref="GhostPredictionSwitchingQueues"/> 时添加
    /// </summary>
    [WriteGroup(typeof(LocalToWorld))]
    public struct SwitchPredictionSmoothing : IComponentData
    {
        /// <summary>
        /// Ghost 在世界空间中的初始位置
        /// </summary>
        public float3 InitialPosition;
        /// <summary>
        /// Ghost 在世界空间中的初始旋转
        /// </summary>
        public quaternion InitialRotation;
        /// <summary>
        /// 应用于当前 Transform 的平滑比例，始终位于 0 到 1 之间
        /// </summary>
        public float CurrentFactor;
        /// <summary>
        /// 过渡持续秒数，在添加 Component 时设置并保持不变
        /// </summary>
        public float Duration;
        /// <summary>
        /// 将该 Component 添加到 Entity 时的 System 版本
        /// </summary>
        public uint SkipVersion;
    }

    /// <summary>
    /// <para>管理所有具有 <see cref="SwitchPredictionSmoothing"/> Component 的 Ghost 预测模式过渡</para>
    /// <para>
    /// 该 System 通过修改 Entity 的 <see cref="LocalToWorld"/> 矩阵为 Ghost 应用视觉平滑
    /// 过渡完成后移除 <see cref="SwitchPredictionSmoothing"/> Component
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [BurstCompile]
    public partial struct SwitchPredictionSmoothingSystem : ISystem
    {
        EntityQuery m_SwitchPredictionSmoothingQuery;

        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<LocalTransform> m_TransformHandle;
        ComponentTypeHandle<PostTransformMatrix> m_PostTransformMatrixType;
        ComponentTypeHandle<SwitchPredictionSmoothing> m_SwitchPredictionSmoothingHandle;
        ComponentTypeHandle<LocalToWorld> m_LocalToWorldHandle;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LocalTransform>()
                .WithAllRW<SwitchPredictionSmoothing, LocalToWorld>();
            m_SwitchPredictionSmoothingQuery = state.GetEntityQuery(builder);
            state.RequireForUpdate(m_SwitchPredictionSmoothingQuery);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_TransformHandle = state.GetComponentTypeHandle<LocalTransform>(true);
            m_PostTransformMatrixType = state.GetComponentTypeHandle<PostTransformMatrix>(true);
            m_SwitchPredictionSmoothingHandle = state.GetComponentTypeHandle<SwitchPredictionSmoothing>();
            m_LocalToWorldHandle = state.GetComponentTypeHandle<LocalToWorld>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var deltaTime = SystemAPI.Time.DeltaTime;
            var commandBuffer = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);

            m_EntityTypeHandle.Update(ref state);
            m_TransformHandle.Update(ref state);
            m_PostTransformMatrixType.Update(ref state);
            m_SwitchPredictionSmoothingHandle.Update(ref state);
            m_LocalToWorldHandle.Update(ref state);

            state.Dependency = new SwitchPredictionSmoothingJob
            {
                EntityType = m_EntityTypeHandle,
                TransformType = m_TransformHandle,
                PostTransformMatrixType = m_PostTransformMatrixType,
                SwitchPredictionSmoothingType = m_SwitchPredictionSmoothingHandle,
                LocalToWorldType = m_LocalToWorldHandle,
                DeltaTime = deltaTime,
                AppliedVersion = SystemAPI.GetSingleton<GhostUpdateVersion>().LastSystemVersion,
                CommandBuffer = commandBuffer.AsParallelWriter(),
            }.ScheduleParallel(m_SwitchPredictionSmoothingQuery, state.Dependency);
        }

        [BurstCompile]
        struct SwitchPredictionSmoothingJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle EntityType;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> TransformType;
            [ReadOnly] public ComponentTypeHandle<PostTransformMatrix> PostTransformMatrixType;
            public ComponentTypeHandle<SwitchPredictionSmoothing> SwitchPredictionSmoothingType;
            public ComponentTypeHandle<LocalToWorld> LocalToWorldType;
            public float DeltaTime;
            public uint AppliedVersion;
            public EntityCommandBuffer.ParallelWriter CommandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);

                NativeArray<LocalTransform> transforms = chunk.GetNativeArray(ref TransformType);
                NativeArray<PostTransformMatrix> postTransformMatrices = new NativeArray<PostTransformMatrix>();
                if (chunk.Has(ref PostTransformMatrixType))
                    postTransformMatrices = chunk.GetNativeArray(ref PostTransformMatrixType);

                NativeArray<SwitchPredictionSmoothing> switchPredictionSmoothings = chunk.GetNativeArray(ref SwitchPredictionSmoothingType);
                NativeArray<LocalToWorld> localToWorlds = chunk.GetNativeArray(ref LocalToWorldType);
                NativeArray<Entity> chunkEntities = chunk.GetNativeArray(EntityType);

                for (int i = 0, count = chunk.Count; i < count; ++i)
                {
                    var currentPosition = transforms[i].Position;
                    var currentRotation = transforms[i].Rotation;

                    var smoothing = switchPredictionSmoothings[i];
                    if (smoothing.SkipVersion != AppliedVersion)
                    {
                        if (smoothing.CurrentFactor == 0)
                        {
                            smoothing.InitialPosition = transforms[i].Position - smoothing.InitialPosition;
                            smoothing.InitialRotation = math.mul(math.inverse(smoothing.InitialRotation), transforms[i].Rotation);
                        }

                        smoothing.CurrentFactor = math.saturate(smoothing.CurrentFactor + DeltaTime / smoothing.Duration);
                        switchPredictionSmoothings[i] = smoothing;
                        if (smoothing.CurrentFactor == 1)
                        {
                            CommandBuffer.RemoveComponent<SwitchPredictionSmoothing>(unfilteredChunkIndex, chunkEntities[i]);
                        }

                        currentPosition -= math.lerp(smoothing.InitialPosition, new float3(0,0,0), smoothing.CurrentFactor);
                        currentRotation = math.mul(currentRotation, math.inverse(math.slerp(smoothing.InitialRotation, quaternion.identity, smoothing.CurrentFactor)));
                    }

                    var tr = new float4x4(currentRotation, currentPosition);
                    if (math.distance(transforms[i].Scale, 1f) > 1e-4f)
                    {
                        var scale = float4x4.Scale(new float3(transforms[i].Scale));
                        tr = math.mul(tr, scale);
                    }
                    // TODO: 查找快速判断 postTransformMatrix 是否为单位矩阵的方法
                    if(postTransformMatrices.IsCreated)
                        tr = math.mul(tr, postTransformMatrices[i].Value);

                    localToWorlds[i] = new LocalToWorld { Value = tr };
                }
            }
        }
    }
}
