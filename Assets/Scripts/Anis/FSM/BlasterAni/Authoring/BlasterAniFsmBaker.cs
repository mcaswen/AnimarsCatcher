using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;

public class BlasterAniFsmBaker : Baker<BlasterAniFsmAuthoring>
{
    public override void Bake(BlasterAniFsmAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);

        // Fsm Blob
        BlobBuilder builder;
        BlobBuilderArray<FsmStateNode> states;

        BlasterAniFsmGraphBlobBuilder.AllocateBuilderBase(out builder, out states);
        BlasterAniFsmGraphBlobBuilder.BuildIdleState (ref builder, ref states);
        BlasterAniFsmGraphBlobBuilder.BuildFollowState(ref builder, ref states);
        BlasterAniFsmGraphBlobBuilder.BuildFindState  (ref builder, ref states);
        BlasterAniFsmGraphBlobBuilder.BuildShootState (ref builder, ref states);

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

            PendingEnter = (ActionId)BlasterAniFsmIDs.A_EnterIdle,
            PendingExit = ActionId.None,
        };

        AddComponent(entity, fsm);

        // Blackboard
        var blackboard = AddBuffer<FsmVar>(entity);
        blackboard.EnsureCapacity(math.max(4, authoring.initialBlackboardCapacity)); // 预留容量

    }
}
