using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// Scene 视图中导航网格的预览方式
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
    /// 为一组地面 Layer 指定寻路成本
    /// </summary>
    [Serializable]
    public struct NavigationTerrainCostRule
    {
        [SerializeField] private LayerMask _groundLayers;
        [Min(0.01f)]
        [SerializeField] private float _cost;

        /// <summary>
        /// 创建一条地面 Layer 成本规则
        /// </summary>
        /// <param name="groundLayers">参与匹配的地面 Layer</param>
        /// <param name="cost">进入匹配格子时使用的基础成本</param>
        public NavigationTerrainCostRule(LayerMask groundLayers, float cost)
        {
            _groundLayers = groundLayers;
            _cost = Mathf.Max(0.01f, cost);
        }

        // 这条规则适用的地面 Layer
        public LayerMask GroundLayers => _groundLayers;

        // 匹配后使用的基础寻路成本
        public float Cost => Mathf.Max(0.01f, _cost);

        /// <summary>
        /// 判断指定 Layer 是否属于这条规则
        /// </summary>
        /// <param name="layer">GameObject Layer 索引</param>
        /// <returns>该 Layer 包含在规则中时返回 true</returns>
        public bool Matches(int layer)
        {
            return layer >= 0 && layer < 32 && (_groundLayers.value & (1 << layer)) != 0;
        }
    }

    /// <summary>
    /// 在场景中配置导航网格的烘焙范围、角色尺寸、地面和静态障碍规则
    /// </summary>
    [DisallowMultipleComponent]
    [MovedFrom(true, "AnimarsCatcher.Animars.Navigation.Grid", "AnimarsCatcher.Navigation", "NavigationGridAuthoring")]
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

        // 对齐到完整格子后的实际烘焙范围
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

        // Inspector 中配置的原始世界范围
        public Bounds ConfiguredWorldBounds => _worldBounds;

        // 单个格子的世界边长
        public float CellSize => _cellSize;

        // 可以被识别为地面的 Layer
        public LayerMask GroundLayers => _groundLayers;

        // 会阻挡角色通行的静态物体 Layer
        public LayerMask ObstacleLayers => _obstacleLayers;

        // 角色允许行走的最大坡度
        public float MaximumSlopeDegrees => _maximumSlopeDegrees;

        // 相邻格子可以直接跨越的最大高度差
        public float MaximumStepHeight => _maximumStepHeight;

        // 烘焙时采用的基础角色半径
        public float BaseAgentRadius => _baseAgentRadius;

        // 烘焙时采用的基础角色高度
        public float BaseAgentHeight => _baseAgentHeight;

        // 每个寻路分块包含的格子边长
        public int ClusterSizeInCells => _clusterSizeInCells;

        // 地面没有匹配任何规则时使用的成本
        public float DefaultTerrainCost => _defaultTerrainCost;

        // 已配置的地形成本规则数量
        public int TerrainCostRuleCount => _terrainCostRules?.Length ?? 0;

        // 保存烘焙结果的资产
        public NavigationGridBakeAsset BakeAsset => _bakeAsset;

        // Scene 视图使用的预览模式
        public NavigationGridGizmoMode GizmoMode => _gizmoMode;

        // Scene 视图预览颜色的透明度
        public float VisualizationOpacity => _visualizationOpacity;

        // 预览中是否显示不可行走格子
        public bool ShowBlockedCells => _showBlockedCells;

        // 可通行预览采用的角色半径
        public float VisualizedAgentRadius => _visualizedAgentRadius;

        // 可通行预览额外保留的安全距离
        public float VisualizedAgentMargin => _visualizedAgentMargin;

        // 是否绘制格子之间的可达连接
        public bool ShowNeighborLinks => _showNeighborLinks;

        // Scene 视图最多绘制的格子数量
        public int MaximumGizmoCells => _maximumGizmoCells;

        // 按格子大小向下对齐后的导航网格尺寸
        public int2 GridDimensions => new int2(
            Mathf.Max(1, Mathf.FloorToInt(_worldBounds.size.x / _cellSize)),
            Mathf.Max(1, Mathf.FloorToInt(_worldBounds.size.z / _cellSize)));

        /// <summary>
        /// 按配置顺序查找指定地面 Layer 的寻路成本
        /// </summary>
        /// <param name="layer">地面 Collider 的 GameObject Layer</param>
        /// <returns>第一条匹配规则的成本；没有匹配时返回默认成本</returns>
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
        /// 按 Inspector 中的配置顺序读取地形成本规则
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

        /// <summary>
        /// 关联编辑器烘焙创建或更新的导航网格资产
        /// </summary>
        /// <param name="bakeAsset">当前场景配置对应的导航网格资产</param>
        public void AssignBakeAsset(NavigationGridBakeAsset bakeAsset)
        {
            _bakeAsset = bakeAsset;
        }

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
