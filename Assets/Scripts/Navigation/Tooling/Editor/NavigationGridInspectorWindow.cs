#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    internal sealed class NavigationGridInspectorWindow : EditorWindow
    {
        // 可以从场景中的 Authoring 自动取得资产，也可以直接选择一份独立烘焙资产
        private NavigationGridAuthoring _authoring;
        private NavigationGridBakeAsset _bakeAsset;

        // 滚动位置和当前格子坐标只属于窗口界面，不会写回项目资产
        private Vector2 _scrollPosition;
        private Vector2Int _cellCoordinate;
        private float _agentRadius = 0.35f;
        private float _agentMargin;

        // 同时用资产实例和内容哈希判断统计缓存是否仍然有效
        private int _statisticsAssetId;
        private string _statisticsDataHash = string.Empty;
        private NavigationGridStatistics _statistics;

        // 状态消息只显示最近一次手动烘焙或校验的结果
        private MessageType _statusType = MessageType.None;
        private string _statusMessage = string.Empty;

        [MenuItem("Tools/Animars Catcher/Navigation Grid Inspector")]
        // 从菜单打开窗口时，优先使用 Project 或 Hierarchy 中当前选中的对象
        private static void OpenFromMenu()
        {
            Open(null, null);
        }

        // Inspector 可以直接传入 Authoring 和资产，不必依赖全局选择
        // 窗口只保留对象引用，不复制体积较大的烘焙数据
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

        // 窗口依次显示对象选择、操作按钮、资产摘要、统计和单格检查
        // 资产无效时只显示诊断信息，不读取格子数组
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

        // Authoring 和烘焙资产都可以单独选择
        // 更换 Authoring 时自动切换到它关联的资产，并清除旧统计缓存
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

        // 按钮只调用对应的烘焙或校验方法；资产写入和异常处理由专用流程负责
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

        // 摘要显示场景来源、版本、内容哈希和地图尺寸等资产身份信息
        // 哈希使用可复制文本，方便比较本地与构建环境的结果
        private void DrawAssetSummary()
        {
            // 摘要只读取资产中已有的元数据，不会重新计算哈希或查询场景物理
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

        // 统计结果按内容哈希缓存，避免每次 OnGUI 都遍历全部格子
        // 这里只展示有助于判断烘焙质量的汇总指标
        private void DrawStatistics()
        {
            // 安全距离、坡度和地形成本范围只统计可行走格子
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

        // 单格检查同时显示二维坐标、世界位置和所有烘焙字段
        // 坐标滑块限制在资产范围内，避免越界读取
        private void DrawCellInspector()
        {
            // 角色半径和安全边距只用于当前窗口的可通行检查，不会修改场景配置
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

            // 坐标已经由滑块限制在范围内，可以安全换算成一维索引
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

        // 烘焙成功后切换到输出资产并清除旧统计
        // 失败时将异常写入 Console，并保留当前选择以便修复后重试
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

        // 校验只读取当前资产，不会自动修补或偷偷重新烘焙
        // 简要结果显示在窗口中，异常详情保留在 Console
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

        // 资产实例和内容哈希共同标识一份统计缓存
        // 一次遍历同时统计可行走格子数、安全距离、坡度和地形成本范围
        private void EnsureStatistics()
        {
            // 资产没有格子数据时清空旧统计，避免继续显示上一份资产的结果
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

            // 最小值先设为无穷；没有可行走格子时统一显示为 0
            var statistics = new NavigationGridStatistics
            {
                MinimumClearance = float.PositiveInfinity,
                MinimumSlope = float.PositiveInfinity,
            };

            // 使用 double 累加，降低大地图计算平均值时的精度损失
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

            // 全部统计完成后再一次性更新缓存，避免 OnGUI 读到尚未计算完整的数据
            _statistics = statistics;
            _statisticsAssetId = assetId;
            _statisticsDataHash = _bakeAsset.DataHash;
        }

        // 更换资产或修改资产内容后都要清除统计缓存
        private void InvalidateStatistics()
        {
            _statisticsAssetId = 0;
            _statisticsDataHash = string.Empty;
            _statistics = default;
        }

        // 只有结构完整且至少包含一个格子的资产才能进入详情检查
        private static bool HasInspectableData(NavigationGridBakeAsset bakeAsset)
        {
            return bakeAsset != null &&
                   bakeAsset.Width > 0 &&
                   bakeAsset.Height > 0 &&
                   bakeAsset.CellCount == bakeAsset.Width * bakeAsset.Height;
        }

        // 哈希使用可选择文本框，方便复制比较
        private static void DrawHash(string label, string value)
        {
            EditorGUILayout.LabelField(label);
            EditorGUILayout.SelectableLabel(
                EmptyAsDash(value),
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }

        // 缺失的元数据统一显示短横线，与未绘制字段区分开
        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        // 世界坐标使用固定小数位，方便人工比较相邻格子
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
