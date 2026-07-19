using System.Diagnostics;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using AnimarsCatcher.Gameplay;

namespace AnimarsCatcher.Player.Input
{
    /// <summary>
    /// 在客户端把网络输入中的鼠标点击转换为带版本号的射线请求
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MovementClickInputSystem : ISystem
    {
        /// <summary>
        /// 等待点击请求单例完成烘焙
        /// </summary>
        /// <param name="state">系统运行状态</param>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MovementClickRequest>();
        }

        /// <summary>
        /// 在左键按下的服务器 Tick 记录屏幕坐标并递增请求版本
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            RefRW<MovementClickRequest> request = SystemAPI.GetSingletonRW<MovementClickRequest>();
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();

            foreach (var playInput in SystemAPI.Query<RefRO<PlayerInput>>())
            {
                if (!playInput.ValueRO.LeftMousePressed.IsSet(networkTime.ServerTick.SerializedData))
                    continue;

                // 版本号让射线和 RPC 系统可以独立判断是否已经消费
                request.ValueRW.Version += 1;
                request.ValueRW.ScreenPosition = playInput.ValueRO.MousePosition;

                break;
            }
        }
    }
}
