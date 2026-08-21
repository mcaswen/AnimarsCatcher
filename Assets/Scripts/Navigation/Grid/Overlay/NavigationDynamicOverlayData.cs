using Unity.Entities;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 一个格子受到动态障碍影响后的运行时修正；静态地形仍保存在只读 NavigationGridBlob 中
    /// </summary>
    public struct NavigationDynamicOverlayCell : IBufferElementData
    {
        // 当前挡住该格子的动态障碍数量
        public int BlockCount;

        // 进入该格子时额外增加的移动成本
        public float ExtraCost;

        // 动态障碍占用的空间，需要从静态可用空间中扣除
        public float ClearanceReduction;

        // 最近一次实际修改该格子的更新版本
        public uint Version;
    }

    /// <summary>
    /// 记录寻路分块最近一次受到动态障碍影响的版本
    /// </summary>
    public struct NavigationDynamicOverlayCluster : IBufferElementData
    {
        // 最近一次影响该分块的更新版本
        public uint Version;

        // 更新时命中的周边格子数，仅用于检查影响范围
        public int AffectedCellCount;
    }

    /// <summary>
    /// 服务端动态障碍提交的一次局部变化；同一障碍添加和移除时必须提交相反的数值
    /// </summary>
    public struct NavigationDynamicOverlayDelta : IBufferElementData
    {
        // 受到该障碍影响的格子
        public int CellIndex;

        // 添加障碍时增加，移除同一障碍时减去相同数量
        public int BlockCountDelta;

        // 添加和移除时必须使用数值相反的成本变化
        public float ExtraCostDelta;

        // 添加和移除时必须使用数值相反的空间缩减变化
        public float ClearanceReductionDelta;

        // 障碍来源提供的诊断编号，不影响合并顺序
        public uint SourceId;
    }

    /// <summary>
    /// 记录动态障碍层的当前版本和最近一次更新数量
    /// </summary>
    public struct NavigationDynamicOverlayState : IComponentData
    {
        // 任一格子实际变化时递增的全局版本
        public uint Version;

        // 最近一次更新实际修改的格子数
        public int LastUpdatedCellCount;

        // 最近一次更新影响的分块数
        public int LastUpdatedClusterCount;

        // 缓冲区已经按当前导航网格初始化时为 1
        public byte Initialized;
    }

    /// <summary>
    /// 标记寻路任务是否正在读取动态障碍缓冲区，避免此时调整缓冲区结构
    /// </summary>
    public struct NavigationGridJobActivity : IComponentData
    {
        // 普通 A* 任务正在读取动态障碍数据时为 1
        public byte PathJobActive;

        // 分层寻路或 Flow Field 任务正在读取动态障碍数据时为 1
        public byte FlowFieldJobActive;
    }
}
