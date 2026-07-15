#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Animars.Navigation.Grid.Editor
{
    internal sealed class NavigationGridInspectorWindow : EditorWindow
    {
        private NavigationGridAuthoring _authoring;
        private NavigationGridBakeAsset _bakeAsset;
        private Vector2 _scrollPosition;
        private Vector2Int _cellCoordinate;
        private float _agentRadius = 0.35f;
        private float _agentMargin;
        private int _statisticsAssetId;
        private string _statisticsDataHash = string.Empty;
        private NavigationGridStatistics _statistics;
        private MessageType _statusType = MessageType.None;
        private string _statusMessage = string.Empty;

        [MenuItem("Tools/Animars Catcher/Navigation Grid Inspector")]
        private static void OpenFromMenu()
        {
            Open(null, null);
        }

        internal static void Open(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset)
        {
            NavigationGridInspectorWindow window = GetWindow<NavigationGridInspectorWindow>(
                "Grid Inspector");
            window.minSize = new Vector2(420f, 520f);
            window._authoring = authoring;
            window._bakeAsset = bakeAsset != null ? bakeAsset : authoring?.BakeAsset;
            window.InvalidateStatistics();
            window.Show();
            window.Focus();
        }

        private void OnGUI()
        {
            DrawSelection();
            DrawCommands();

            if (!string.IsNullOrWhiteSpace(_statusMessage))
            {
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }

            if (!HasInspectableData(_bakeAsset))
            {
                EditorGUILayout.HelpBox("请选择包含有效 Cell 数据的 Grid 资产", MessageType.Info);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawAssetSummary();
            DrawStatistics();
            DrawCellInspector();
            EditorGUILayout.EndScrollView();
        }

        private void DrawSelection()
        {
            EditorGUI.BeginChangeCheck();
            NavigationGridAuthoring nextAuthoring =
                (NavigationGridAuthoring)EditorGUILayout.ObjectField(
                    "Authoring",
                    _authoring,
                    typeof(NavigationGridAuthoring),
                    true);
            if (EditorGUI.EndChangeCheck())
            {
                _authoring = nextAuthoring;
                _bakeAsset = _authoring != null ? _authoring.BakeAsset : null;
                _statusMessage = string.Empty;
                InvalidateStatistics();
            }

            EditorGUI.BeginChangeCheck();
            NavigationGridBakeAsset nextBakeAsset =
                (NavigationGridBakeAsset)EditorGUILayout.ObjectField(
                    "Bake Asset",
                    _bakeAsset,
                    typeof(NavigationGridBakeAsset),
                    false);
            if (EditorGUI.EndChangeCheck())
            {
                _bakeAsset = nextBakeAsset;
                _statusMessage = string.Empty;
                InvalidateStatistics();
            }
        }

        private void DrawCommands()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           _authoring == null || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("烘焙"))
                    {
                        BakeSelectedAuthoring();
                    }

                    if (GUILayout.Button("校验"))
                    {
                        ValidateSelectedAuthoring();
                    }
                }

                using (new EditorGUI.DisabledScope(_bakeAsset == null))
                {
                    if (GUILayout.Button("定位资产"))
                    {
                        Selection.activeObject = _bakeAsset;
                        EditorGUIUtility.PingObject(_bakeAsset);
                    }
                }

                if (GUILayout.Button("刷新统计"))
                {
                    InvalidateStatistics();
                    EnsureStatistics();
                }
            }
        }

        private void DrawAssetSummary()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("资产信息", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("来源场景", EmptyAsDash(_bakeAsset.SourceScenePath));
            EditorGUILayout.LabelField(
                "数据版本",
                $"{_bakeAsset.DataVersion}  工具 {_bakeAsset.ToolVersion}");
            EditorGUILayout.LabelField(
                "尺寸",
                $"{_bakeAsset.Width} x {_bakeAsset.Height}  共 {_bakeAsset.CellCount:N0} Cell");
            EditorGUILayout.LabelField("Cell 大小", _bakeAsset.CellSize.ToString("0.####"));
            EditorGUILayout.LabelField(
                "基础 Agent",
                $"半径 {_bakeAsset.BaseAgentRadius:0.####}  高度 {_bakeAsset.BaseAgentHeight:0.####}");
            EditorGUILayout.LabelField("Cluster 大小", _bakeAsset.ClusterSizeInCells.ToString());
            EditorGUILayout.LabelField("Region 数量", _bakeAsset.RegionCount.ToString());
            EditorGUILayout.LabelField("世界中心", FormatVector(_bakeAsset.WorldBounds.center));
            EditorGUILayout.LabelField("世界尺寸", FormatVector(_bakeAsset.WorldBounds.size));

            DrawHash("Geometry Hash", _bakeAsset.GeometryHash);
            DrawHash("Parameter Hash", _bakeAsset.ParameterHash);
            DrawHash("Data Hash", _bakeAsset.DataHash);
        }

        private void DrawStatistics()
        {
            EnsureStatistics();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("数据统计", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("可行走", $"{_statistics.WalkableCount:N0}");
            EditorGUILayout.LabelField("阻挡", $"{_statistics.BlockedCount:N0}");
            EditorGUILayout.LabelField(
                "可行走比例",
                _bakeAsset.CellCount > 0
                    ? ((float)_statistics.WalkableCount / _bakeAsset.CellCount).ToString("P2")
                    : "0.00%");

            if (_statistics.WalkableCount > 0)
            {
                EditorGUILayout.LabelField(
                    "Clearance 最小 / 平均 / 最大",
                    $"{_statistics.MinimumClearance:0.####} / " +
                    $"{_statistics.AverageClearance:0.####} / " +
                    $"{_statistics.MaximumClearance:0.####}");
                EditorGUILayout.LabelField(
                    "坡度 最小 / 平均 / 最大",
                    $"{_statistics.MinimumSlope:0.##} / " +
                    $"{_statistics.AverageSlope:0.##} / " +
                    $"{_statistics.MaximumSlope:0.##}");
            }
        }

        private void DrawCellInspector()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Cell 检查", EditorStyles.boldLabel);

            int maximumX = Mathf.Max(0, _bakeAsset.Width - 1);
            int maximumZ = Mathf.Max(0, _bakeAsset.Height - 1);
            _cellCoordinate.x = EditorGUILayout.IntSlider(
                "X",
                Mathf.Clamp(_cellCoordinate.x, 0, maximumX),
                0,
                maximumX);
            _cellCoordinate.y = EditorGUILayout.IntSlider(
                "Z",
                Mathf.Clamp(_cellCoordinate.y, 0, maximumZ),
                0,
                maximumZ);

            _agentRadius = Mathf.Max(
                0f,
                EditorGUILayout.FloatField("Agent 半径", _agentRadius));
            _agentMargin = Mathf.Max(
                0f,
                EditorGUILayout.FloatField("安全边距", _agentMargin));

            int cellIndex = _cellCoordinate.x + _cellCoordinate.y * _bakeAsset.Width;
            NavigationGridCellData cell = _bakeAsset.GetCell(cellIndex);
            Vector3 center = NavigationGridBakeUtility.GetCellCenter(_bakeAsset, cellIndex);
            bool canOccupy = _bakeAsset.CanAgentOccupy(
                cellIndex,
                _agentRadius,
                _agentMargin);

            EditorGUILayout.LabelField("索引", cellIndex.ToString());
            EditorGUILayout.LabelField("世界中心", FormatVector(center));
            EditorGUILayout.LabelField("可行走", cell.Walkable ? "是" : "否");
            EditorGUILayout.LabelField("Agent 可占用", canOccupy ? "是" : "否");
            EditorGUILayout.LabelField("高度", cell.Height.ToString("0.####"));
            EditorGUILayout.LabelField("法线", FormatVector(cell.SurfaceNormal));
            EditorGUILayout.LabelField("坡度", cell.SlopeDegrees.ToString("0.##"));
            EditorGUILayout.LabelField("地形成本", cell.TerrainCost.ToString("0.####"));
            EditorGUILayout.LabelField("Clearance", cell.Clearance.ToString("0.####"));
            EditorGUILayout.LabelField("Region", cell.RegionId.ToString());
            EditorGUILayout.LabelField("Cluster", cell.ClusterId.ToString());
            EditorGUILayout.LabelField("邻接", cell.NeighborMask.ToString());

            if (GUILayout.Button("在 Scene 视图定位"))
            {
                SceneView.lastActiveSceneView?.LookAt(
                    center,
                    Quaternion.Euler(60f, 0f, 0f),
                    Mathf.Max(2f, _bakeAsset.CellSize * 8f));
                SceneView.RepaintAll();
            }
        }

        private void BakeSelectedAuthoring()
        {
            try
            {
                _bakeAsset = NavigationGridBakeUtility.Bake(_authoring);
                _statusType = MessageType.Info;
                _statusMessage = "烘焙完成";
                InvalidateStatistics();
                SceneView.RepaintAll();
            }
            catch (Exception exception)
            {
                _statusType = MessageType.Error;
                _statusMessage = exception.Message;
                Debug.LogException(exception, _authoring);
            }
        }

        private void ValidateSelectedAuthoring()
        {
            try
            {
                bool valid = NavigationGridBakeUtility.TryValidateCurrentAsset(
                    _authoring,
                    out string message);
                _statusType = valid ? MessageType.Info : MessageType.Warning;
                _statusMessage = message;

                if (_authoring != null && _authoring.BakeAsset != null)
                {
                    _bakeAsset = _authoring.BakeAsset;
                    InvalidateStatistics();
                }
            }
            catch (Exception exception)
            {
                _statusType = MessageType.Error;
                _statusMessage = exception.Message;
                Debug.LogException(exception, _authoring);
            }
        }

        private void EnsureStatistics()
        {
            if (!HasInspectableData(_bakeAsset))
            {
                _statistics = default;
                return;
            }

            int assetId = _bakeAsset.GetInstanceID();
            if (_statisticsAssetId == assetId &&
                string.Equals(
                    _statisticsDataHash,
                    _bakeAsset.DataHash,
                    StringComparison.Ordinal))
            {
                return;
            }

            var statistics = new NavigationGridStatistics
            {
                MinimumClearance = float.PositiveInfinity,
                MinimumSlope = float.PositiveInfinity,
            };

            double clearanceTotal = 0d;
            double slopeTotal = 0d;
            for (int index = 0; index < _bakeAsset.CellCount; index++)
            {
                NavigationGridCellData cell = _bakeAsset.GetCell(index);
                if (!cell.Walkable)
                {
                    statistics.BlockedCount++;
                    continue;
                }

                statistics.WalkableCount++;
                statistics.MinimumClearance = Mathf.Min(
                    statistics.MinimumClearance,
                    cell.Clearance);
                statistics.MaximumClearance = Mathf.Max(
                    statistics.MaximumClearance,
                    cell.Clearance);
                statistics.MinimumSlope = Mathf.Min(statistics.MinimumSlope, cell.SlopeDegrees);
                statistics.MaximumSlope = Mathf.Max(statistics.MaximumSlope, cell.SlopeDegrees);
                clearanceTotal += cell.Clearance;
                slopeTotal += cell.SlopeDegrees;
            }

            if (statistics.WalkableCount > 0)
            {
                statistics.AverageClearance =
                    (float)(clearanceTotal / statistics.WalkableCount);
                statistics.AverageSlope = (float)(slopeTotal / statistics.WalkableCount);
            }
            else
            {
                statistics.MinimumClearance = 0f;
                statistics.MinimumSlope = 0f;
            }

            _statistics = statistics;
            _statisticsAssetId = assetId;
            _statisticsDataHash = _bakeAsset.DataHash;
        }

        private void InvalidateStatistics()
        {
            _statisticsAssetId = 0;
            _statisticsDataHash = string.Empty;
            _statistics = default;
        }

        private static bool HasInspectableData(NavigationGridBakeAsset bakeAsset)
        {
            return bakeAsset != null &&
                   bakeAsset.Width > 0 &&
                   bakeAsset.Height > 0 &&
                   bakeAsset.CellCount == bakeAsset.Width * bakeAsset.Height;
        }

        private static void DrawHash(string label, string value)
        {
            EditorGUILayout.LabelField(label);
            EditorGUILayout.SelectableLabel(
                EmptyAsDash(value),
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
        }

        private struct NavigationGridStatistics
        {
            public int WalkableCount;
            public int BlockedCount;
            public float MinimumClearance;
            public float MaximumClearance;
            public float AverageClearance;
            public float MinimumSlope;
            public float MaximumSlope;
            public float AverageSlope;
        }
    }
}
#endif
