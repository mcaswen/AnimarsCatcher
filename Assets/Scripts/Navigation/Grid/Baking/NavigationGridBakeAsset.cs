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
        /// <summary>
        /// 当前可读取的 Grid 数据格式版本
        /// </summary>
        public const int CurrentDataVersion = 2;

        /// <summary>
        /// 当前 Grid 烘焙工具版本
        /// </summary>
        public const string CurrentToolVersion = "1.1.0";

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
        [SerializeField] private int _regionCount;
        [SerializeField] private NavigationGridCellData[] _cells = Array.Empty<NavigationGridCellData>();

        // 以下属性提供只读检查入口，资产内容只能由完整烘焙结果整体替换
        /// <summary>
        /// 获取来源场景的稳定 GUID
        /// </summary>
        public string SourceSceneGuid => _sourceSceneGuid;

        /// <summary>
        /// 获取来源场景的项目相对路径
        /// </summary>
        public string SourceScenePath => _sourceScenePath;

        /// <summary>
        /// 获取参与采样的场景几何 Hash
        /// </summary>
        public string GeometryHash => _geometryHash;

        /// <summary>
        /// 获取 Authoring 参数 Hash
        /// </summary>
        public string ParameterHash => _parameterHash;

        /// <summary>
        /// 获取最终 Cell 数据 Hash
        /// </summary>
        public string DataHash => _dataHash;

        /// <summary>
        /// 获取生成当前资产的工具版本
        /// </summary>
        public string ToolVersion => _toolVersion;

        /// <summary>
        /// 获取当前资产的数据格式版本
        /// </summary>
        public int DataVersion => _dataVersion;

        /// <summary>
        /// 获取 Grid 覆盖的世界包围盒
        /// </summary>
        public Bounds WorldBounds => _worldBounds;

        /// <summary>
        /// 获取单个 Cell 的世界边长
        /// </summary>
        public float CellSize => _cellSize;

        /// <summary>
        /// 获取生成基础可行走图时使用的 Agent 半径
        /// </summary>
        public float BaseAgentRadius => _baseAgentRadius;

        /// <summary>
        /// 获取生成基础可行走图时使用的 Agent 高度
        /// </summary>
        public float BaseAgentHeight => _baseAgentHeight;

        /// <summary>
        /// 获取 Grid 在世界 X 轴上的 Cell 数量
        /// </summary>
        public int Width => _width;

        /// <summary>
        /// 获取 Grid 在世界 Z 轴上的 Cell 数量
        /// </summary>
        public int Height => _height;

        /// <summary>
        /// 获取每个 Cluster 的 Cell 边长
        /// </summary>
        public int ClusterSizeInCells => _clusterSizeInCells;

        /// <summary>
        /// 获取静态连通区域数量
        /// </summary>
        public int RegionCount => _regionCount;

        /// <summary>
        /// 获取当前资产保存的 Cell 数量
        /// </summary>
        public int CellCount => _cells?.Length ?? 0;

        /// <summary>
        /// 判断资产结构和版本是否可供 Baker 使用
        /// </summary>
        public bool IsUsable =>
            _dataVersion == CurrentDataVersion &&
            string.Equals(_toolVersion, CurrentToolVersion, StringComparison.Ordinal) &&
            _width > 0 &&
            _height > 0 &&
            _cellSize > 0f &&
            _baseAgentRadius > 0f &&
            _baseAgentHeight >= _baseAgentRadius * 2f &&
            _cells != null &&
            _cells.Length == _width * _height &&
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
            _regionCount = result.RegionCount;
            _cells = result.Cells ?? Array.Empty<NavigationGridCellData>();
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
        public string SourceSceneGuid = string.Empty;
        public string SourceScenePath = string.Empty;
        public string GeometryHash = string.Empty;
        public string ParameterHash = string.Empty;
        public string DataHash = string.Empty;
        public string ToolVersion = NavigationGridBakeAsset.CurrentToolVersion;
        public int DataVersion = NavigationGridBakeAsset.CurrentDataVersion;
        public Bounds WorldBounds;
        public float CellSize;
        public float BaseAgentRadius;
        public float BaseAgentHeight;
        public int Width;
        public int Height;
        public int ClusterSizeInCells;
        public int RegionCount;
        public NavigationGridCellData[] Cells = Array.Empty<NavigationGridCellData>();
    }
}
