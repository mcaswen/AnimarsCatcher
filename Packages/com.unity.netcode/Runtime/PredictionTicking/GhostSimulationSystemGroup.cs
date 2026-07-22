using System;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>同时存在于客户端和服务器 World 中，也会存在于 Local World 中以支持单机输入
    /// 这是包含大部分 NetCode 系统的核心 Group
    /// 其职责较多，大致可分为以下类别</para>
    /// <para>- 输入收集：<see cref="GhostInputSystemGroup"/></para>
    /// <para>- Command 处理：<see cref="CommandSendSystemGroup"/></para>
    /// <para>- Ghost 预测与模拟：<see cref="PredictedSimulationSystemGroup"/></para>
    /// <para>- Ghost 生成：参见 <see cref="GhostSpawnClassificationSystem"/>、<see cref="GhostSpawnSystemGroup"/>、<see cref="GhostSpawnSystem"/> 和 <see cref="GhostDespawnSystem"/></para>
    /// <para>- Ghost 复制：<see cref="GhostCollection"/>、<see cref="GhostSendSystem"/>、<see cref="GhostReceiveSystem"/> 和 <see cref="GhostUpdateSystem"/></para>
    /// <para>
    /// 通常，所有需要模拟或操作 Ghost Entity 的系统都应添加到此 Group
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.LocalSimulation,
        WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    public partial class GhostSimulationSystemGroup : ComponentSystemGroup
    {
    }

}
