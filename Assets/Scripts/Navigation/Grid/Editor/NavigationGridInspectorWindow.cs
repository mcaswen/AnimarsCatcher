#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    internal sealed class NavigationGridInspectorWindow : EditorWindow
    {
        // 选择状态允许从 Authoring 跳转到资产也允许直接检查独立资产
        private NavigationGridAuthoring _authoring;
        private NavigationGridBakeAsset _bakeAsset;

        // 滚动和 Cell 坐标属于窗口交互状态 不写回任何项目资产
        private Vector2 _scrollPosition;
        private Vector2Int _cellCoordinate;
        private float _agentRadius = 0.35f;
        private float _agentMargin;

        // 统计缓存使用资产实例与 Data Hash 双键识别内容变化
        private int _statisticsAssetId;
        private string _statisticsDataHash = string.Empty;
        private NavigationGridStatistics _statistics;

        // 状态消息只反映最近一次显式烘焙或校验命令
        private MessageType _statusType = MessageType.None;
        private string _statusMessage = string.Empty;

        [MenuItem("Tools/Animars Catcher/Navigation Grid Inspector")]
        // 菜单入口在没有显式上下文时尝试使用当前选择对象
        private static void OpenFromMenu()
        {
            Open(null, null);
        }

        // Inspector 可传入明确 Authoring 和资产避免依赖全局 Selection
        // 窗口只保存引用而不复制体积较大的烘焙数据
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

        // 窗口按选择 命令 摘要 统计和 Cell 检查顺序组织
        // 数据无效时只显示诊断入口不访问 Cell 数组
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

        // 选择区允许独立指定 Authoring 或 Bake Asset
        // Authoring 变化时同步其当前资产并使统计缓存失效
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

        // 命令按钮只负责触发烘焙和校验入口
        // 具体异常处理和资产写入留在专用方法中
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

        // 摘要显示来源 版本 Hash 和尺寸等不可变资产身份
        // 长 Hash 保持可复制以便比较构建与本地结果
        private void DrawAssetSummary()
        {
            // 摘要只读取持久化元数据 不触发重新 Hash 或物理查询
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

        // 统计值按 Data Hash 缓存避免每次 OnGUI 重扫全部 Cell
        // 只展示能够帮助判断烘焙质量的聚合指标
        private void DrawStatistics()
        {
            // 聚合统计只针对可行走 Cell 计算 Clearance 和坡度范围
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

        // Cell 索引同时显示二维坐标 世界位置和全部派生字段
        // 输入范围受资产 CellCount 限制防止检查窗口越界
        private void DrawCellInspector()
        {
            // Agent 半径与边距只用于即时占用检查 不修改 Authoring 配置
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

            // 坐标在滑块阶段已经钳制 因而行主序索引必定位于资产范围
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

        // 烘焙成功后重新绑定输出资产并清除旧统计
        // 异常写入 Console 且保留当前选择供修复后重试
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

        // 校验只读当前资产 不会自动修补或触发隐式烘焙
        // 结果通过通知显示并保留详细 Console 异常
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

        // Data Hash 和资产实例共同作为统计缓存身份
        // 遍历时同时计算可行走数 Clearance 和地形成本范围
        private void EnsureStatistics()
        {
            // 无数据时清空旧统计避免切换到空资产后继续显示历史值
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

            // 最小值以正无穷初始化并在没有可行走 Cell 时统一回退到零
            var statistics = new NavigationGridStatistics
            {
                MinimumClearance = float.PositiveInfinity,
                MinimumSlope = float.PositiveInfinity,
            };

            // 累加使用 double 降低大型 Grid 求平均时的精度损失
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

            // 完整遍历结束后一次性发布缓存 防止 OnGUI 读取半成品
            _statistics = statistics;
            _statisticsAssetId = assetId;
            _statisticsDataHash = _bakeAsset.DataHash;
        }

        // 所有可能改变资产引用或内容的操作都必须清除统计缓存
        private void InvalidateStatistics()
        {
            _statisticsAssetId = 0;
            _statisticsDataHash = string.Empty;
            _statistics = default;
        }

        // 检查入口只接受结构完整且至少包含一个 Cell 的资产
        private static bool HasInspectableData(NavigationGridBakeAsset bakeAsset)
        {
            return bakeAsset != null &&
                   bakeAsset.Width > 0 &&
                   bakeAsset.Height > 0 &&
                   bakeAsset.CellCount == bakeAsset.Width * bakeAsset.Height;
        }

        // Hash 使用可选择文本字段便于开发者复制比较
        private static void DrawHash(string label, string value)
        {
            EditorGUILayout.LabelField(label);
            EditorGUILayout.SelectableLabel(
                EmptyAsDash(value),
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }

        // 空元数据统一显示短横线避免与未绘制字段混淆
        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        // 固定小数精度让相邻 Cell 坐标更容易人工比较
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
