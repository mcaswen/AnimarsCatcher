using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.VisualScripting;

public class AniMovementFsmBaker : Baker<AniMovementFsmAuthoring>
{
    public override void Bake(AniMovementFsmAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);

        // Fsm Blob
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

        // Fsm
        var fsm = new Fsm
        {
            Current = (StateId)authoring.initialState,
            Next = (StateId)authoring.initialState,

            HasPending = 1,
            TimeInState = 0f,

            PendingEnter = (ActionId)AniMovementFsmIDs.A_EnterIdle,
            PendingExit = ActionId.None,
        };

        AddComponent(entity, fsm);

        // Blackboard
        var blackboard = AddBuffer<FsmVar>(entity);
        blackboard.EnsureCapacity(math.max(4, authoring.initialBlackboardCapacity)); // 预留容量

        // Move
        AddComponent(entity, new AniMoveIntent { DesiredVelocity = float3.zero });

    }
}
