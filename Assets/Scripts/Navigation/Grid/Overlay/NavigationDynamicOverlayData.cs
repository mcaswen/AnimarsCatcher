using Unity.Entities;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 保存单个 Cell 的运行时动态占用和代价修正
    /// 静态地形数据仍然只读保存在 NavigationGridBlob 中
    /// </summary>
    public struct NavigationDynamicOverlayCell : IBufferElementData
    {
        // 重叠障碍的阻挡引用数量
        public int BlockCount;

        // 进入该 Cell 时叠加的非负移动成本
        public float ExtraCost;

        // 从静态 Clearance 中扣除的非负世界空间距离
        public float ClearanceReduction;

        // 最近一次有效修改该 Cell 的批次版本
        public uint Version;
    }

    /// <summary>
    /// 保存受动态障碍影响的 Cluster 版本
    /// </summary>
    public struct NavigationDynamicOverlayCluster : IBufferElementData
    {
        // 最近一次局部影响该 Cluster 的批次版本
        public uint Version;

        // 累计命中的外围 Cell 数量，仅用于诊断更新范围
        public int AffectedCellCount;
    }

    /// <summary>
    /// 由服务端障碍物生产者追加到 Grid 的局部 Overlay 差量
    /// BlockCountDelta 必须成对使用，不能把重叠障碍压缩成 bool
    /// </summary>
    public struct NavigationDynamicOverlayDelta : IBufferElementData
    {
        // 障碍来源覆盖的 Cell 索引
        public int CellIndex;

        // 添加障碍使用正数，移除同一障碍使用对应负数
        public int BlockCountDelta;

        // 添加和移除必须保持成对的成本差量
        public float ExtraCostDelta;

        // 添加和移除必须保持成对的 Clearance 差量
        public float ClearanceReductionDelta;

        // 生产者提供的稳定诊断标识，不参与合并排序
        public uint SourceId;
    }

    /// <summary>
    /// 保存 Overlay 的全局诊断版本和本次局部更新统计
    /// </summary>
    public struct NavigationDynamicOverlayState : IComponentData
    {
        // 任一 Cell 有效变化时递增的全局诊断版本
        public uint Version;

        // 最近批次实际修改的 Cell 数量
        public int LastUpdatedCellCount;

        // 最近批次首次标记的 Cluster 数量
        public int LastUpdatedClusterCount;

        // Buffer 已与当前 Grid 拓扑完成对齐时为一
        public byte Initialized;
    }

    /// <summary>
    /// 防止 Overlay 在路径或 Field Job 持有 Buffer NativeArray 时发生结构性写入
    /// </summary>
    public struct NavigationGridJobActivity : IComponentData
    {
        // 普通 A 星 Job 正在只读 Overlay Buffer 时为一
        public byte PathJobActive;

        // HPA 星与 Flow Field Job 正在只读 Overlay Buffer 时为一
        public byte FlowFieldJobActive;
    }
}
