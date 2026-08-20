using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 保存编辑器 Physics 采样生成的可检查 Grid 数据
    /// </summary>
    [CreateAssetMenu(
        fileName = "SO_NavigationGrid",
        menuName = "Animars Catcher/Navigation/Grid Bake Asset")]
    [MovedFrom(true, "AnimarsCatcher.Animars.Navigation.Grid", "AnimarsCatcher.Navigation", "NavigationGridBakeAsset")]
    public sealed class NavigationGridBakeAsset : ScriptableObject
    {
        // 当前可读取的 Grid 数据格式版本
        public const int CurrentDataVersion = 3;

        // 当前 Grid 烘焙工具版本
        public const string CurrentToolVersion = "1.2.0";

        [SerializeField] private string _sourceSceneGuid = string.Empty;
        [SerializeField] private string _sourceScenePath = string.Empty;
        [SerializeField] private string _geometryHash = string.Empty;
        [SerializeField] private string _parameterHash = string.Empty;
        [SerializeField] private string _dataHash = string.Empty;
        [SerializeField] private string _toolVersion = string.Empty;
        [SerializeField] private int _dataVersion;
        [SerializeField] private Bounds _worldBounds;
        [SerializeField] private float _cellSize;
        [SerializeField] private float _baseAgentRadius;
        [SerializeField] private float _baseAgentHeight;
        [SerializeField] private int _width;
        [SerializeField] private int _height;
        [SerializeField] private int _clusterSizeInCells;
        [SerializeField] private int _clusterWidth;
        [SerializeField] private int _clusterHeight;
        [SerializeField] private int _regionCount;
        [SerializeField] private NavigationGridCellData[] _cells = Array.Empty<NavigationGridCellData>();
        [SerializeField] private NavigationGridClusterData[] _clusters = Array.Empty<NavigationGridClusterData>();
        [SerializeField] private NavigationGridPortalData[] _portals = Array.Empty<NavigationGridPortalData>();
        [SerializeField] private NavigationGridPortalNodeData[] _portalNodes = Array.Empty<NavigationGridPortalNodeData>();
        [SerializeField] private NavigationGridAbstractEdgeData[] _abstractEdges = Array.Empty<NavigationGridAbstractEdgeData>();
        [SerializeField] private int[] _clusterPortalNodeIndices = Array.Empty<int>();

        // 以下属性提供只读检查入口，资产内容只能由完整烘焙结果整体替换
        // 来源场景的稳定 GUID
        public string SourceSceneGuid => _sourceSceneGuid;

        // 来源场景的项目相对路径
        public string SourceScenePath => _sourceScenePath;

        // 参与采样的场景几何 Hash
        public string GeometryHash => _geometryHash;

        // Authoring 参数 Hash
        public string ParameterHash => _parameterHash;

        // 最终 Cell 数据 Hash
        public string DataHash => _dataHash;

        // 生成当前资产的工具版本
        public string ToolVersion => _toolVersion;

        // 当前资产的数据格式版本
        public int DataVersion => _dataVersion;

        // Grid 覆盖的世界包围盒
        public Bounds WorldBounds => _worldBounds;

        // 单个 Cell 的世界边长
        public float CellSize => _cellSize;

        // 生成基础可行走图时使用的 Agent 半径
        public float BaseAgentRadius => _baseAgentRadius;

        // 生成基础可行走图时使用的 Agent 高度
        public float BaseAgentHeight => _baseAgentHeight;

        // Grid 在世界 X 轴上的 Cell 数量
        public int Width => _width;

        // Grid 在世界 Z 轴上的 Cell 数量
        public int Height => _height;

        // 每个 Cluster 的 Cell 边长
        public int ClusterSizeInCells => _clusterSizeInCells;

        // Grid 在 X 轴生成的 Cluster 数量
        public int ClusterWidth => _clusterWidth;

        // Grid 在 Z 轴生成的 Cluster 数量
        public int ClusterHeight => _clusterHeight;

        // 静态连通区域数量
        public int RegionCount => _regionCount;

        // 当前资产保存的 Cell 数量
        public int CellCount => _cells?.Length ?? 0;

        // 当前资产保存的 Cluster 数量
        public int ClusterCount => _clusters?.Length ?? 0;

        // 当前资产保存的 Portal 数量
        public int PortalCount => _portals?.Length ?? 0;

        // 当前资产保存的 Portal Node 数量
        public int PortalNodeCount => _portalNodes?.Length ?? 0;

        // 当前资产保存的抽象有向边数量
        public int AbstractEdgeCount => _abstractEdges?.Length ?? 0;

        // 当前资产保存的 Cluster 节点索引数量
        public int ClusterPortalNodeIndexCount => _clusterPortalNodeIndices?.Length ?? 0;

        // 资产结构和版本是否可供 Baker 使用
        public bool IsUsable =>
            _dataVersion == CurrentDataVersion &&
            string.Equals(_toolVersion, CurrentToolVersion, StringComparison.Ordinal) &&
            _width > 0 &&
            _height > 0 &&
            _cellSize > 0f &&
            _baseAgentRadius > 0f &&
            _baseAgentHeight >= _baseAgentRadius * 2f &&
            _clusterSizeInCells > 0 &&
            _clusterWidth == Mathf.CeilToInt((float)_width / _clusterSizeInCells) &&
            _clusterHeight == Mathf.CeilToInt((float)_height / _clusterSizeInCells) &&
            _cells != null &&
            _cells.Length == _width * _height &&
            _clusters != null &&
            _clusters.Length == _clusterWidth * _clusterHeight &&
            _portals != null &&
            _portalNodes != null &&
            _portalNodes.Length == _portals.Length * 2 &&
            _abstractEdges != null &&
            _clusterPortalNodeIndices != null &&
            _clusterPortalNodeIndices.Length == _portalNodes.Length &&
            Mathf.Abs(_worldBounds.size.x - _width * _cellSize) <= 0.0001f &&
            Mathf.Abs(_worldBounds.size.z - _height * _cellSize) <= 0.0001f &&
            IsValidHash(_geometryHash) &&
            IsValidHash(_parameterHash) &&
            IsValidHash(_dataHash);

        /// <summary>
        /// 按稳定一维索引读取 Cell
        /// </summary>
        /// <param name="index">Cell 的行主序索引</param>
        /// <returns>对应的可检查 Cell 数据</returns>
        public NavigationGridCellData GetCell(int index)
        {
            if (_cells == null)
            {
                throw new InvalidOperationException("Navigation Grid asset has no cell data");
            }

            return _cells[index];
        }

        /// <summary>
        /// 按稳定索引读取 Cluster
        /// </summary>
        public NavigationGridClusterData GetCluster(int index) => _clusters[index];

        /// <summary>
        /// 按稳定索引读取 Portal
        /// </summary>
        public NavigationGridPortalData GetPortal(int index) => _portals[index];

        /// <summary>
        /// 按稳定索引读取 Portal Node
        /// </summary>
        public NavigationGridPortalNodeData GetPortalNode(int index) => _portalNodes[index];

        /// <summary>
        /// 按稳定索引读取抽象有向边
        /// </summary>
        public NavigationGridAbstractEdgeData GetAbstractEdge(int index) => _abstractEdges[index];

        /// <summary>
        /// 按连续切片索引读取 Cluster 的 Portal Node
        /// </summary>
        public int GetClusterPortalNodeIndex(int index) => _clusterPortalNodeIndices[index];

        /// <summary>
        /// 判断指定半径是否能占用目标 Cell
        /// </summary>
        /// <param name="index">Cell 的行主序索引</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="margin">额外安全边距</param>
        /// <returns>Cell 可行走且 Clearance 足够时返回 true</returns>
        public bool CanAgentOccupy(int index, float agentRadius, float margin = 0f)
        {
            NavigationGridCellData cell = GetCell(index);
            float requiredClearance =
                Mathf.Max(0f, agentRadius - _baseAgentRadius) +
                Mathf.Max(0f, margin);
            return cell.Walkable && cell.Clearance >= requiredClearance;
        }

        /// <summary>
        /// 用一次完整烘焙结果替换资产内容
        /// </summary>
        /// <param name="result">已经完成算法处理和 Hash 计算的烘焙结果</param>
        public void ApplyBakeResult(NavigationGridBakeResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            _sourceSceneGuid = result.SourceSceneGuid;
            _sourceScenePath = result.SourceScenePath;
            _geometryHash = result.GeometryHash;
            _parameterHash = result.ParameterHash;
            _dataHash = result.DataHash;
            _toolVersion = result.ToolVersion;
            _dataVersion = result.DataVersion;
            _worldBounds = result.WorldBounds;
            _cellSize = result.CellSize;
            _baseAgentRadius = result.BaseAgentRadius;
            _baseAgentHeight = result.BaseAgentHeight;
            _width = result.Width;
            _height = result.Height;
            _clusterSizeInCells = result.ClusterSizeInCells;
            _clusterWidth = result.ClusterWidth;
            _clusterHeight = result.ClusterHeight;
            _regionCount = result.RegionCount;
            _cells = result.Cells ?? Array.Empty<NavigationGridCellData>();
            _clusters = result.Clusters ?? Array.Empty<NavigationGridClusterData>();
            _portals = result.Portals ?? Array.Empty<NavigationGridPortalData>();
            _portalNodes = result.PortalNodes ?? Array.Empty<NavigationGridPortalNodeData>();
            _abstractEdges = result.AbstractEdges ?? Array.Empty<NavigationGridAbstractEdgeData>();
            _clusterPortalNodeIndices =
                result.ClusterPortalNodeIndices ?? Array.Empty<int>();
        }

        private static bool IsValidHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                bool decimalDigit = character >= '0' && character <= '9';
                bool lowerHex = character >= 'a' && character <= 'f';
                bool upperHex = character >= 'A' && character <= 'F';
                if (!decimalDigit && !lowerHex && !upperHex)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 在编辑器烘焙流程与可检查资产之间传递完整结果
    /// </summary>
    public sealed class NavigationGridBakeResult
    {
        // 来源场景和数据版本元数据
        public string SourceSceneGuid = string.Empty;
        public string SourceScenePath = string.Empty;
        public string GeometryHash = string.Empty;
        public string ParameterHash = string.Empty;
        public string DataHash = string.Empty;
        public string ToolVersion = NavigationGridBakeAsset.CurrentToolVersion;
        public int DataVersion = NavigationGridBakeAsset.CurrentDataVersion;

        // Grid 形状和基础 Agent 参数
        public Bounds WorldBounds;
        public float CellSize;
        public float BaseAgentRadius;
        public float BaseAgentHeight;
        public int Width;
        public int Height;
        public int ClusterSizeInCells;
        public int ClusterWidth;
        public int ClusterHeight;
        public int RegionCount;

        // 可直接写入资产的 Cell 与分层图数组
        public NavigationGridCellData[] Cells = Array.Empty<NavigationGridCellData>();
        public NavigationGridClusterData[] Clusters = Array.Empty<NavigationGridClusterData>();
        public NavigationGridPortalData[] Portals = Array.Empty<NavigationGridPortalData>();
        public NavigationGridPortalNodeData[] PortalNodes = Array.Empty<NavigationGridPortalNodeData>();
        public NavigationGridAbstractEdgeData[] AbstractEdges = Array.Empty<NavigationGridAbstractEdgeData>();
        public int[] ClusterPortalNodeIndices = Array.Empty<int>();
    }
}
