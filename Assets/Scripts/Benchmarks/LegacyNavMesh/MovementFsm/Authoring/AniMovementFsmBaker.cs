using AnimarsCatcher.Core.Fsm;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 将旧 NavMesh 移动状态机配置烘焙为基线 ECS 数据
    /// </summary>
    public class AniMovementFsmBaker : Baker<AniMovementFsmAuthoring>
    {
        public override void Bake(AniMovementFsmAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            // 构建所有 Entity 共享的不可变状态图
            BlobBuilder builder;
            BlobBuilderArray<FsmStateNode> states;

            AniMovementFsmGraphBlobBuilder.AllocateBuilderBase(out builder, out states);
            AniMovementFsmGraphBlobBuilder.BuildIdleState(ref builder, ref states);
            AniMovementFsmGraphBlobBuilder.BuildFollowState(ref builder, ref states);
            AniMovementFsmGraphBlobBuilder.BuildFindState(ref builder, ref states);
            AniMovementFsmGraphBlobBuilder.BuildMoveToState(ref builder, ref states);

            var graphRef = builder.CreateBlobAssetReference<FsmGraph>(Allocator.Persistent);
            builder.Dispose();

            AddComponent(entity, new FsmGraphRef { Value = graphRef });

            // 初始化首帧需要执行进入动作的状态机数据
            var fsm = new Fsm
            {
                Current = (StateId)authoring.initialState,
                Next = (StateId)authoring.initialState,

                HasPending = 1,
                TimeInState = 0f,

                PendingEnter = (ActionId)AniMovementFsmIds.EnterIdleActionId,
                PendingExit = ActionId.None,
            };

            AddComponent(entity, fsm);

            // 预留黑板容量，降低常用变量写入时的扩容次数
            var blackboard = AddBuffer<FsmVar>(entity);
            blackboard.EnsureCapacity(math.max(4, authoring.initialBlackboardCapacity));

            // 导航系统从零速度意图开始接管移动
            AddComponent(entity, new AniMoveIntent { DesiredVelocity = float3.zero });

        }
    }
}
