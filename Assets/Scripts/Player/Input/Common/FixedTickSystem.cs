namespace AnimarsCatcher.Player
{
    using Unity.Burst;
    using Unity.Entities;

    /// <summary>
    /// 保存当前本地固定帧序号
    /// </summary>
    public struct FixedTickState : IComponentData
    {
        public uint Tick;
    }

    /// <summary>
    /// 在固定步长模拟结束时递增本地固定帧计数
    /// </summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    [BurstCompile]
    public partial struct FixedTickSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<FixedTickState>())
            {
                Entity singletonEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(singletonEntity, new FixedTickState());
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            ref FixedTickState fixedTickState = ref SystemAPI.GetSingletonRW<FixedTickState>().ValueRW;
            fixedTickState.Tick++;
        }
    }
}
