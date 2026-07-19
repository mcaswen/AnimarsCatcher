namespace AnimarsCatcher.Player
{
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel;
    using Unity.Burst;
    using Unity.Entities;
    using UnityEngine;

    /// <summary>
    /// 在固定步长模拟结束时递增本地固定帧计数
    /// </summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    [BurstCompile]
    public partial struct FixedTickSystem : ISystem
    {
        /// <summary>
        /// 保存当前本地固定帧序号
        /// </summary>
        public struct Singleton : IComponentData
        {
            public uint Tick;
        }

        /// <summary>
        /// 创建唯一的固定帧计数单例
        /// </summary>
        /// <param name="state">系统状态</param>
        public void OnCreate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<Singleton>())
            {
                Entity singletonEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(singletonEntity, new Singleton());
            }
        }

        /// <summary>
        /// 在每次固定步长结束时推进计数
        /// </summary>
        /// <param name="state">系统状态</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            ref Singleton singleton = ref SystemAPI.GetSingletonRW<Singleton>().ValueRW;
            singleton.Tick++;
        }
    }
}
