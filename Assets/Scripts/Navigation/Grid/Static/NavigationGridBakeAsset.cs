using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 保存编辑器从场景物理几何中烘焙出的导航网格和分层寻路数据
    /// </summary>
    [CreateAssetMenu(
        fileName = "SO_NavigationGrid",
        menuName = "Animars Catcher/Navigation/Grid Bake Asset")]
    [MovedFrom(true, "AnimarsCatcher.Animars.Navigation.Grid", "AnimarsCatcher.Navigation", "NavigationGridBakeAsset")]
    public sealed class NavigationGridBakeAsset : ScriptableObject
    {
        // 当前代码支持的数据格式版本
        public const int CurrentDataVersion = 3;

        // 当前导航网格烘焙工具版本
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

        // 以下属性仅供查看；资产内容只能由一次完整烘焙整体替换
        // 来源场景的 GUID
        public string SourceSceneGuid => _sourceSceneGuid;

        // 来源场景在项目中的相对路径
        public string SourceScenePath => _sourceScenePath;

        // 参与采样的场景几何哈希
        public string GeometryHash => _geometryHash;

        // 烘焙配置参数哈希
        public string ParameterHash => _parameterHash;

        // 完整烘焙结果的内容哈希
        public string DataHash => _dataHash;

        // 生成该资产时使用的工具版本
        public string ToolVersion => _toolVersion;

        // 该资产的数据格式版本
        public int DataVersion => _dataVersion;

        // 导航网格覆盖的世界范围
        public Bounds WorldBounds => _worldBounds;

        // 单个格子的世界边长
        public float CellSize => _cellSize;

        // 烘焙基础可行走区域时采用的角色半径
        public float BaseAgentRadius => _baseAgentRadius;

        // 烘焙基础可行走区域时采用的角色高度
        public float BaseAgentHeight => _baseAgentHeight;

        // X 方向的格子数
        public int Width => _width;

        // Z 方向的格子数
        public int Height => _height;

        // 每个寻路分块包含的格子边长
        public int ClusterSizeInCells => _clusterSizeInCells;

        // X 方向的分块数
        public int ClusterWidth => _clusterWidth;

        // Z 方向的分块数
        public int ClusterHeight => _clusterHeight;

        // 静态连通区域数
        public int RegionCount => _regionCount;

        // 资产中的格子数
        public int CellCount => _cells?.Length ?? 0;

        // 资产中的寻路分块数
        public int ClusterCount => _clusters?.Length ?? 0;

        // 资产中的分块入口数
        public int PortalCount => _portals?.Length ?? 0;

        // 资产中的入口节点数
        public int PortalNodeCount => _portalNodes?.Length ?? 0;

        // 资产中的抽象有向连接数
        public int AbstractEdgeCount => _abstractEdges?.Length ?? 0;

        // 分块引用的入口节点索引总数
        public int ClusterPortalNodeIndexCount => _clusterPortalNodeIndices?.Length ?? 0;

        // 资产结构和版本是否能被当前 Baker 读取
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
        /// 按一维索引读取格子
        /// </summary>
        /// <param name="index">Cell 的行主序索引</param>
        /// <returns>对应的格子烘焙数据</returns>
        public NavigationGridCellData GetCell(int index)
        {
            if (_cells == null)
            {
                throw new InvalidOperationException("Navigation Grid asset has no cell data");
            }

            return _cells[index];
        }

        /// <summary>
        /// 按索引读取寻路分块
        /// </summary>
        public NavigationGridClusterData GetCluster(int index) => _clusters[index];

        /// <summary>
        /// 按索引读取分块入口
        /// </summary>
        public NavigationGridPortalData GetPortal(int index) => _portals[index];

        /// <summary>
        /// 按索引读取入口节点
        /// </summary>
        public NavigationGridPortalNodeData GetPortalNode(int index) => _portalNodes[index];

        /// <summary>
        /// 按索引读取抽象图中的有向连接
        /// </summary>
        public NavigationGridAbstractEdgeData GetAbstractEdge(int index) => _abstractEdges[index];

        /// <summary>
        /// 从连续索引表中读取某个分块连接的入口节点
        /// </summary>
        public int GetClusterPortalNodeIndex(int index) => _clusterPortalNodeIndices[index];

        /// <summary>
        /// 判断指定体型的角色能否安全站在目标格子中
        /// </summary>
        /// <param name="index">Cell 的行主序索引</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="margin">额外安全边距</param>
        /// <returns>格子可行走且可用空间足够时返回 true</returns>
        public bool CanAgentOccupy(int index, float agentRadius, float margin = 0f)
        {
            NavigationGridCellData cell = GetCell(index);
            float requiredClearance =
                Mathf.Max(0f, agentRadius - _baseAgentRadius) +
                Mathf.Max(0f, margin);
            return cell.Walkable && cell.Clearance >= requiredClearance;
        }

        /// <summary>
        /// 使用新的完整烘焙结果替换资产中的所有数据
        /// </summary>
        /// <param name="result">已经完成连通计算、分层构建和哈希计算的烘焙结果</param>
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
    /// 编辑器烘焙流程生成的完整结果，可一次性写入 NavigationGridBakeAsset
    /// </summary>
    public sealed class NavigationGridBakeResult
    {
        // 来源场景、工具版本和内容哈希
        public string SourceSceneGuid = string.Empty;
        public string SourceScenePath = string.Empty;
        public string GeometryHash = string.Empty;
        public string ParameterHash = string.Empty;
        public string DataHash = string.Empty;
        public string ToolVersion = NavigationGridBakeAsset.CurrentToolVersion;
        public int DataVersion = NavigationGridBakeAsset.CurrentDataVersion;

        // 导航网格尺寸和基础角色参数
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

        // 可直接写入资产的格子数据和分层寻路数组
        public NavigationGridCellData[] Cells = Array.Empty<NavigationGridCellData>();
        public NavigationGridClusterData[] Clusters = Array.Empty<NavigationGridClusterData>();
        public NavigationGridPortalData[] Portals = Array.Empty<NavigationGridPortalData>();
        public NavigationGridPortalNodeData[] PortalNodes = Array.Empty<NavigationGridPortalNodeData>();
        public NavigationGridAbstractEdgeData[] AbstractEdges = Array.Empty<NavigationGridAbstractEdgeData>();
        public int[] ClusterPortalNodeIndices = Array.Empty<int>();
    }
}
