using System;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Animars.Movement.Grid
{
    /// <summary>
    /// 定义 Scene 视图中 Grid 数据的显示模式
    /// </summary>
    public enum NavigationGridGizmoMode
    {
        Disabled,
        Walkability,
        Clearance,
        Region,
        Slope,
        TerrainCost,
        AgentOccupancy,
    }

    /// <summary>
    /// 将地面 Layer 映射为后续路径搜索使用的成本
    /// </summary>
    [Serializable]
    public struct NavigationTerrainCostRule
    {
        [SerializeField] private LayerMask _groundLayers;
        [Min(0.01f)]
        [SerializeField] private float _cost;

        // 以下成员向编辑器烘焙流程公开只读成本规则
        /// <summary>
        /// 创建一个地面 Layer 成本规则
        /// </summary>
        /// <param name="groundLayers">参与匹配的地面 Layer</param>
        /// <param name="cost">进入匹配 Cell 的基础成本</param>
        public NavigationTerrainCostRule(LayerMask groundLayers, float cost)
        {
            _groundLayers = groundLayers;
            _cost = Mathf.Max(0.01f, cost);
        }

        /// <summary>
        /// 获取参与匹配的地面 Layer
        /// </summary>
        public LayerMask GroundLayers => _groundLayers;

        /// <summary>
        /// 获取匹配后的基础地形成本
        /// </summary>
        public float Cost => Mathf.Max(0.01f, _cost);

        /// <summary>
        /// 判断指定 Layer 是否使用当前规则
        /// </summary>
        /// <param name="layer">GameObject Layer 索引</param>
        /// <returns>Layer 位包含在规则中时返回 true</returns>
        public bool Matches(int layer)
        {
            return layer >= 0 && layer < 32 && (_groundLayers.value & (1 << layer)) != 0;
        }
    }

    /// <summary>
    /// 配置编辑器 Physics Grid 烘焙范围和静态采样参数
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NavigationGridAuthoring : MonoBehaviour
    {
        [Header("Grid")]
        [SerializeField] private Bounds _worldBounds = new Bounds(
            new Vector3(0f, 4f, 0f),
            new Vector3(64f, 8f, 64f));

        [Min(0.05f)]
        [SerializeField] private float _cellSize = 0.5f;

        [SerializeField] private LayerMask _groundLayers = 1 << 11;
        [SerializeField] private LayerMask _obstacleLayers = 1;

        [Header("Agent")]
        [Range(0f, 89f)]
        [SerializeField] private float _maximumSlopeDegrees = 40f;

        [Min(0f)]
        [SerializeField] private float _maximumStepHeight = 0.5f;

        [Min(0.01f)]
        [SerializeField] private float _baseAgentRadius = 0.35f;

        [Min(0.02f)]
        [SerializeField] private float _baseAgentHeight = 1.5f;

        [Min(1)]
        [SerializeField] private int _clusterSizeInCells = 16;

        [Header("Terrain Cost")]
        [Min(0.01f)]
        [SerializeField] private float _defaultTerrainCost = 1f;

        [SerializeField] private NavigationTerrainCostRule[] _terrainCostRules =
            Array.Empty<NavigationTerrainCostRule>();

        [Header("Output")]
        [Tooltip("为空时首次烘焙会在 Assets/SO/Navigation 下自动创建")]
        [SerializeField] private NavigationGridBakeAsset _bakeAsset;

        [Header("Scene Visualization")]
        [SerializeField] private NavigationGridGizmoMode _gizmoMode =
            NavigationGridGizmoMode.Walkability;

        [Range(0.05f, 1f)]
        [SerializeField] private float _visualizationOpacity = 0.55f;

        [SerializeField] private bool _showBlockedCells = true;

        [Min(0f)]
        [SerializeField] private float _visualizedAgentRadius = 0.35f;

        [Min(0f)]
        [SerializeField] private float _visualizedAgentMargin;

        [SerializeField] private bool _showNeighborLinks;

        [Tooltip("超过上限时按固定步长抽样显示，避免 Scene 视图卡顿")]
        [Min(64)]
        [SerializeField] private int _maximumGizmoCells = 4096;

        // 以下属性只暴露烘焙输入 不允许外部绕过 Inspector 修改序列化状态
        /// <summary>
        /// 获取按完整 Cell 向下对齐后的有效世界包围盒
        /// </summary>
        public Bounds WorldBounds
        {
            get
            {
                int2 dimensions = GridDimensions;
                Vector3 minimum = _worldBounds.min;
                Vector3 size = _worldBounds.size;
                size.x = dimensions.x * _cellSize;
                size.z = dimensions.y * _cellSize;
                return new Bounds(minimum + size * 0.5f, size);
            }
        }

        /// <summary>
        /// 获取 Inspector 中尚未对齐的原始配置包围盒
        /// </summary>
        public Bounds ConfiguredWorldBounds => _worldBounds;

        /// <summary>
        /// 获取单个 Cell 的世界边长
        /// </summary>
        public float CellSize => _cellSize;

        /// <summary>
        /// 获取允许作为地面的 Layer
        /// </summary>
        public LayerMask GroundLayers => _groundLayers;

        /// <summary>
        /// 获取参与静态占用检测的 Layer
        /// </summary>
        public LayerMask ObstacleLayers => _obstacleLayers;

        /// <summary>
        /// 获取允许行走的最大坡度角
        /// </summary>
        public float MaximumSlopeDegrees => _maximumSlopeDegrees;

        /// <summary>
        /// 获取允许建立邻接的最大高度差
        /// </summary>
        public float MaximumStepHeight => _maximumStepHeight;

        /// <summary>
        /// 获取静态占用采样使用的基准 Agent 半径
        /// </summary>
        public float BaseAgentRadius => _baseAgentRadius;

        /// <summary>
        /// 获取静态占用采样使用的基准 Agent 高度
        /// </summary>
        public float BaseAgentHeight => _baseAgentHeight;

        /// <summary>
        /// 获取每个 Cluster 的 Cell 边长
        /// </summary>
        public int ClusterSizeInCells => _clusterSizeInCells;

        /// <summary>
        /// 获取未匹配规则时使用的地形成本
        /// </summary>
        public float DefaultTerrainCost => _defaultTerrainCost;

        /// <summary>
        /// 获取有序地形成本规则数量
        /// </summary>
        public int TerrainCostRuleCount => _terrainCostRules?.Length ?? 0;

        /// <summary>
        /// 获取烘焙输出资产
        /// </summary>
        public NavigationGridBakeAsset BakeAsset => _bakeAsset;

        /// <summary>
        /// 获取 Scene 视图的 Grid 显示模式
        /// </summary>
        public NavigationGridGizmoMode GizmoMode => _gizmoMode;

        /// <summary>
        /// 获取 Scene 视图覆盖层透明度
        /// </summary>
        public float VisualizationOpacity => _visualizationOpacity;

        /// <summary>
        /// 获取是否在覆盖层中显示阻挡 Cell
        /// </summary>
        public bool ShowBlockedCells => _showBlockedCells;

        /// <summary>
        /// 获取可占用性预览使用的 Agent 半径
        /// </summary>
        public float VisualizedAgentRadius => _visualizedAgentRadius;

        /// <summary>
        /// 获取可占用性预览使用的额外安全边距
        /// </summary>
        public float VisualizedAgentMargin => _visualizedAgentMargin;

        /// <summary>
        /// 获取是否显示 Cell 邻接连线
        /// </summary>
        public bool ShowNeighborLinks => _showNeighborLinks;

        /// <summary>
        /// 获取 Scene 视图允许显示的最大 Cell 数量
        /// </summary>
        public int MaximumGizmoCells => _maximumGizmoCells;

        /// <summary>
        /// 获取按 Cell Size 向下对齐后的 Grid 尺寸
        /// </summary>
        public int2 GridDimensions => new int2(
            Mathf.Max(1, Mathf.FloorToInt(_worldBounds.size.x / _cellSize)),
            Mathf.Max(1, Mathf.FloorToInt(_worldBounds.size.z / _cellSize)));

        /// <summary>
        /// 按规则顺序解析指定地面 Layer 的成本
        /// </summary>
        /// <param name="layer">地面 Collider 的 GameObject Layer</param>
        /// <returns>首个匹配规则的成本或默认成本</returns>
        public float ResolveTerrainCost(int layer)
        {
            if (_terrainCostRules != null)
            {
                for (int i = 0; i < _terrainCostRules.Length; i++)
                {
                    if (_terrainCostRules[i].Matches(layer))
                    {
                        return _terrainCostRules[i].Cost;
                    }
                }
            }

            return Mathf.Max(0.01f, _defaultTerrainCost);
        }

        /// <summary>
        /// 按稳定顺序读取地形成本规则
        /// </summary>
        /// <param name="index">规则序列索引</param>
        /// <returns>对应的地形 Layer 成本规则</returns>
        public NavigationTerrainCostRule GetTerrainCostRule(int index)
        {
            if (_terrainCostRules == null)
            {
                throw new InvalidOperationException("Terrain cost rules are not initialized");
            }

            return _terrainCostRules[index];
        }

#if UNITY_EDITOR
        /// <summary>
        /// 绑定编辑器烘焙创建或更新的输出资产
        /// </summary>
        /// <param name="bakeAsset">与当前 Authoring 对应的 Grid 资产</param>
        public void AssignBakeAsset(NavigationGridBakeAsset bakeAsset)
        {
            _bakeAsset = bakeAsset;
        }
#endif

        private void OnValidate()
        {
            Vector3 size = _worldBounds.size;
            size.x = Mathf.Max(0.05f, size.x);
            size.y = Mathf.Max(0.05f, size.y);
            size.z = Mathf.Max(0.05f, size.z);
            _worldBounds.size = size;

            _cellSize = Mathf.Max(0.05f, _cellSize);
            _maximumSlopeDegrees = Mathf.Clamp(_maximumSlopeDegrees, 0f, 89f);
            _maximumStepHeight = Mathf.Max(0f, _maximumStepHeight);
            _baseAgentRadius = Mathf.Max(0.01f, _baseAgentRadius);
            _baseAgentHeight = Mathf.Max(_baseAgentRadius * 2f, _baseAgentHeight);
            _clusterSizeInCells = Mathf.Max(1, _clusterSizeInCells);
            _defaultTerrainCost = Mathf.Max(0.01f, _defaultTerrainCost);
            _visualizationOpacity = Mathf.Clamp(_visualizationOpacity, 0.05f, 1f);
            _visualizedAgentRadius = Mathf.Max(0f, _visualizedAgentRadius);
            _visualizedAgentMargin = Mathf.Max(0f, _visualizedAgentMargin);
            _maximumGizmoCells = Mathf.Max(64, _maximumGizmoCells);
        }
    }
}
