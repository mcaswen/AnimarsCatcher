#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 将烘焙后的格子批量绘制成 Scene 视图覆盖层，便于检查通行、坡度、成本和连通区域
    /// </summary>
    [InitializeOnLoad]
    public static class NavigationGridVisualizationRenderer
    {
        // 连续数值被量化为有限个颜色档位，避免每个格子使用独立材质状态
        // 连通区域循环使用固定色板，必要时结合边界线和格子详情区分重复颜色
        private const int ContinuousBucketCount = 16;
        private const int RegionBucketCount = 16;

        // 缓存 Shader 属性 ID，避免 Scene 视图重绘时重复按字符串查找
        private static readonly int _colorPropertyId = Shader.PropertyToID("_Color");

        // 不可行走和可行走格子在各模式中使用一致的基础颜色，切换预览时更容易识别
        private static readonly Color _blockedColor = new Color32(216, 74, 88, 255);
        private static readonly Color _walkableColor = new Color32(59, 164, 114, 255);
        private static readonly Color _warningColor = new Color32(239, 171, 67, 255);
        // 连通区域使用人工选定的分类色板，避免随机 HSV 颜色太暗或彼此难以区分
        private static readonly Color[] _regionPalette =
        {
            new Color32(72, 120, 226, 255),
            new Color32(48, 166, 116, 255),
            new Color32(226, 148, 58, 255),
            new Color32(139, 101, 220, 255),
            new Color32(224, 86, 96, 255),
            new Color32(32, 164, 168, 255),
            new Color32(211, 91, 148, 255),
            new Color32(108, 164, 61, 255),
            new Color32(62, 151, 209, 255),
            new Color32(211, 111, 60, 255),
            new Color32(92, 107, 199, 255),
            new Color32(50, 174, 139, 255),
            new Color32(197, 153, 50, 255),
            new Color32(194, 84, 110, 255),
            new Color32(53, 165, 196, 255),
            new Color32(165, 91, 174, 255),
        };

        // 每个 Authoring 缓存一份覆盖层 Mesh；影响几何或颜色的数据变化时整体重建
        private static readonly Dictionary<int, CacheEntry> _cacheByAuthoringId = new();
        private static Material _overlayMaterial;

        static NavigationGridVisualizationRenderer()
        {
            // 缓存持有临时 Mesh 和材质，在程序集重载、编辑器退出或 Hierarchy 变化时统一释放
            // Hierarchy 变化可能替换 Authoring 实例，因此直接清空全部预览缓存
            AssemblyReloadEvents.beforeAssemblyReload += ClearCache;
            EditorApplication.quitting += ClearCache;
            EditorApplication.hierarchyChanged += ClearCache;
        }

        /// <summary>
        /// 绘制指定导航网格的 Scene 覆盖层，并尽量复用已生成的 Mesh
        /// </summary>
        /// <param name="authoring">提供显示参数的 Grid Authoring</param>
        /// <param name="bakeAsset">提供烘焙 Cell 的 Grid 资产</param>
        public static void Draw(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset)
        {
            // Scene 每次重绘只提交缓存 Mesh，不重复生成顶点
            // 是否需要重建由 GetOrBuildCache 统一判断
            CacheEntry cacheEntry = GetOrBuildCache(authoring, bakeAsset);
            if (cacheEntry?.Mesh == null)
            {
                return;
            }

            Material overlayMaterial = GetOrCreateOverlayMaterial();
            if (overlayMaterial == null)
            {
                return;
            }

            // 每个子网格都单独设置材质颜色，避免不同渲染管线忽略 Gizmos 全局颜色
            // 没有格子的颜色档位不会提交绘制，减少无效 Draw Call
            for (int bucketIndex = 0; bucketIndex < cacheEntry.HasGeometry.Length; bucketIndex++)
            {
                if (!cacheEntry.HasGeometry[bucketIndex])
                {
                    continue;
                }

                overlayMaterial.SetColor(
                    _colorPropertyId,
                    ResolveBucketColor(
                        authoring.GizmoMode,
                        bucketIndex,
                        authoring.VisualizationOpacity));
                if (!overlayMaterial.SetPass(0))
                {
                    continue;
                }

                Graphics.DrawMeshNow(
                    cacheEntry.Mesh,
                    Matrix4x4.identity,
                    bucketIndex);
            }
        }

        /// <summary>
        /// 在 Inspector 中显示当前预览模式的颜色含义
        /// </summary>
        /// <param name="mode">当前覆盖层显示模式</param>
        public static void DrawLegend(NavigationGridGizmoMode mode)
        {
            // 图例和覆盖层调用同一套颜色函数，调色板变化时两处会同步更新
            if (mode == NavigationGridGizmoMode.Disabled)
            {
                return;
            }

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("图例", GUILayout.Width(30f));
                DrawLegendItem("阻挡", ResolveBucketColor(mode, 0, 1f));

                // 连续数值模式只显示低、中、高三个代表颜色
                switch (mode)
                {
                    case NavigationGridGizmoMode.Clearance:
                        DrawLegendItem("低余量", ResolveBucketColor(mode, 1, 1f));
                        DrawLegendItem(
                            "高余量",
                            ResolveBucketColor(mode, ContinuousBucketCount, 1f));
                        break;

                    case NavigationGridGizmoMode.Region:
                        DrawLegendItem("区域 A", ResolveBucketColor(mode, 1, 1f));
                        DrawLegendItem("区域 B", ResolveBucketColor(mode, 2, 1f));
                        break;

                    case NavigationGridGizmoMode.Slope:
                        DrawLegendItem("平缓", ResolveBucketColor(mode, 1, 1f));
                        DrawLegendItem(
                            "陡峭",
                            ResolveBucketColor(mode, ContinuousBucketCount, 1f));
                        break;

                    case NavigationGridGizmoMode.TerrainCost:
                        DrawLegendItem("低成本", ResolveBucketColor(mode, 1, 1f));
                        DrawLegendItem(
                            "高成本",
                            ResolveBucketColor(mode, ContinuousBucketCount, 1f));
                        break;

                    case NavigationGridGizmoMode.AgentOccupancy:
                        DrawLegendItem("空间不足", ResolveBucketColor(mode, 1, 1f));
                        DrawLegendItem("可占用", ResolveBucketColor(mode, 2, 1f));
                        break;

                    default:
                        DrawLegendItem("可行走", ResolveBucketColor(mode, 1, 1f));
                        break;
                }
            }
        }

        /// <summary>
        /// 根据地图大小和显示预算计算二维抽样步长
        /// </summary>
        /// <param name="width">Grid 宽度</param>
        /// <param name="height">Grid 高度</param>
        /// <param name="maximumCells">允许显示的最大 Cell 数量</param>
        /// <returns>不小于 1 的二维抽样步长</returns>
        public static int GetSampleStride(int width, int height, int maximumCells)
        {
            // 宽、高和预算先限制为正数，避免除零或无限循环
            // X、Z 两个方向使用同一步长，使抽样后的格子形状接近原地图
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            maximumCells = Mathf.Max(1, maximumCells);

            int stride = 1;
            // 使用 long 计算样本总数，防止超大地图乘法溢出
            while ((long)CeilDivide(width, stride) * CeilDivide(height, stride) > maximumCells)
            {
                stride++;
            }

            return stride;
        }

        // 缓存同时依赖 Authoring 实例、资产内容和影响 Mesh 的显示参数
        // 仅透明度变化时可以复用 Mesh，绘制阶段动态修改材质颜色即可
        private static CacheEntry GetOrBuildCache(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset)
        {
            int authoringId = authoring.GetInstanceID();
            if (_cacheByAuthoringId.TryGetValue(authoringId, out CacheEntry current) &&
                current.Matches(authoring, bakeAsset))
            {
                return current;
            }

            current?.Dispose();
            CacheEntry replacement = BuildCache(authoring, bakeAsset);
            _cacheByAuthoringId[authoringId] = replacement;
            return replacement;
        }

        // 将格子按颜色档位合并为子网格，并记录哪些档位真正包含几何
        // 这样既保留颜色含义，也能把 Draw Call 控制在固定范围内
        private static CacheEntry BuildCache(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset)
        {
            // 所有颜色档位共享一份顶点列表，每个子网格只保存自己的三角形索引
            // 共享顶点可减少托管列表和 Mesh 上传占用
            int bucketCount = GetBucketCount(authoring.GizmoMode);
            var vertices = new List<Vector3>();
            var vertexColors = new List<Color32>();
            // 同一颜色档位合并为一个子网格，绘制时无需逐格提交
            var trianglesByBucket = new List<int>[bucketCount];
            for (int bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
            {
                trianglesByBucket[bucketIndex] = new List<int>();
            }

            int stride = GetSampleStride(
                bakeAsset.Width,
                bakeAsset.Height,
                authoring.MaximumGizmoCells);
            // 从每个抽样块中心选格子，避免总从行首取样形成条纹
            int sampleStart = stride / 2;
            ResolveTerrainCostRange(
                bakeAsset,
                stride,
                sampleStart,
                out float minimumTerrainCost,
                out float maximumTerrainCost);

            // 预览方块略小于格子，相邻格之间会留下清晰缝隙
            // 垂直偏移限制在小范围内，减少 Z-Fighting，又不会看起来悬浮过高
            float halfCellSize = bakeAsset.CellSize * 0.47f;
            float verticalOffset = Mathf.Clamp(bakeAsset.CellSize * 0.03f, 0.015f, 0.1f);
            Bounds bounds = bakeAsset.WorldBounds;

            // 顶点直接使用烘焙世界坐标，不依赖 Authoring 的当前 Transform
            for (int z = sampleStart; z < bakeAsset.Height; z += stride)
            {
                for (int x = sampleStart; x < bakeAsset.Width; x += stride)
                {
                    int cellIndex = x + z * bakeAsset.Width;
                    NavigationGridCellData cell = bakeAsset.GetCell(cellIndex);
                    int bucketIndex = ResolveBucketIndex(
                        authoring,
                        bakeAsset,
                        cellIndex,
                        cell,
                        minimumTerrainCost,
                        maximumTerrainCost);
                    if (bucketIndex < 0)
                    {
                        continue;
                    }

                    float centerX = bounds.min.x + (x + 0.5f) * bakeAsset.CellSize;
                    float centerZ = bounds.min.z + (z + 0.5f) * bakeAsset.CellSize;
                    float surfaceY = cell.Height + verticalOffset;
                    int vertexStart = vertices.Count;
                    vertices.Add(new Vector3(centerX - halfCellSize, surfaceY, centerZ - halfCellSize));
                    vertices.Add(new Vector3(centerX - halfCellSize, surfaceY, centerZ + halfCellSize));
                    vertices.Add(new Vector3(centerX + halfCellSize, surfaceY, centerZ + halfCellSize));
                    vertices.Add(new Vector3(centerX + halfCellSize, surfaceY, centerZ - halfCellSize));
                    // Sprite Shader 会将材质颜色乘以顶点色，因此顶点统一使用白色
                    vertexColors.Add(new Color32(255, 255, 255, 255));
                    vertexColors.Add(new Color32(255, 255, 255, 255));
                    vertexColors.Add(new Color32(255, 255, 255, 255));
                    vertexColors.Add(new Color32(255, 255, 255, 255));

                    List<int> triangles = trianglesByBucket[bucketIndex];
                    triangles.Add(vertexStart);
                    triangles.Add(vertexStart + 1);
                    triangles.Add(vertexStart + 2);
                    triangles.Add(vertexStart);
                    triangles.Add(vertexStart + 2);
                    triangles.Add(vertexStart + 3);
                }
            }

            // 顶点数超过 16 位索引上限时自动改用 UInt32
            // 临时预览 Mesh 使用 HideAndDontSave，不会被序列化进场景或资产
            var mesh = new Mesh
            {
                name = $"Navigation Grid Overlay {authoring.GetInstanceID()}",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Count > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16,
            };
            mesh.SetVertices(vertices);
            mesh.SetColors(vertexColors);
            mesh.subMeshCount = bucketCount;

            var hasGeometry = new bool[bucketCount];
            for (int bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
            {
                List<int> triangles = trianglesByBucket[bucketIndex];
                hasGeometry[bucketIndex] = triangles.Count > 0;
                mesh.SetTriangles(triangles, bucketIndex, false);
            }

            // 所有子网格完成后统一计算 Bounds，并将 Mesh 标记为不再修改
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return new CacheEntry(authoring, bakeAsset, mesh, hasGeometry);
        }

        // 预览材质在首次使用时创建，并在本次编辑器会话内复用
        // 找不到所需 Shader 时返回 null，调用方跳过绘制而不是抛出异常
        private static Material GetOrCreateOverlayMaterial()
        {
            if (_overlayMaterial != null)
            {
                return _overlayMaterial;
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogError("Navigation Grid visualization shader is unavailable");
                return null;
            }

            // 内置 Sprite Shader 支持无光照透明混合，颜色不受 Scene 灯光影响
            _overlayMaterial = new Material(shader)
            {
                name = "Navigation Grid Visualization Material",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _overlayMaterial.mainTexture = Texture2D.whiteTexture;
            return _overlayMaterial;
        }

        // 状态类模式直接映射颜色编号；连续数据先归一化，再量化到颜色档位
        // 相同输入必须得到相同档位，缓存 Mesh 和图例才能保持一致
        private static int ResolveBucketIndex(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset,
            int cellIndex,
            NavigationGridCellData cell,
            float minimumTerrainCost,
            float maximumTerrainCost)
        {
            // 0 号档位统一表示不可行走，其余档位由当前预览模式解释
            if (!cell.Walkable)
            {
                return authoring.ShowBlockedCells ? 0 : -1;
            }

            switch (authoring.GizmoMode)
            {
                case NavigationGridGizmoMode.Clearance:
                    float clearanceScale = Mathf.Max(
                        bakeAsset.CellSize,
                        bakeAsset.BaseAgentRadius * 4f);
                    return 1 + QuantizeContinuous(cell.Clearance / clearanceScale);

                case NavigationGridGizmoMode.Region:
                    uint stableRegionHash = unchecked((uint)cell.RegionId * 2654435761u);
                    return 1 + (int)(stableRegionHash % RegionBucketCount);

                case NavigationGridGizmoMode.Slope:
                    float slopeScale = Mathf.Max(1f, authoring.MaximumSlopeDegrees);
                    return 1 + QuantizeContinuous(cell.SlopeDegrees / slopeScale);

                case NavigationGridGizmoMode.TerrainCost:
                    float terrainCostRatio = maximumTerrainCost - minimumTerrainCost <= 0.0001f
                        ? 0f
                        : Mathf.InverseLerp(
                            minimumTerrainCost,
                            maximumTerrainCost,
                            cell.TerrainCost);
                    return 1 + QuantizeContinuous(terrainCostRatio);

                case NavigationGridGizmoMode.AgentOccupancy:
                    return bakeAsset.CanAgentOccupy(
                        cellIndex,
                        authoring.VisualizedAgentRadius,
                        authoring.VisualizedAgentMargin)
                            ? 2
                            : 1;

                default:
                    return 1;
            }
        }

        // 成本范围只统计本次实际绘制的可行走格子
        // 所有样本成本相同时人为留出最小范围，避免归一化除零
        private static void ResolveTerrainCostRange(
            NavigationGridBakeAsset bakeAsset,
            int stride,
            int sampleStart,
            out float minimum,
            out float maximum)
        {
            minimum = float.PositiveInfinity;
            maximum = float.NegativeInfinity;

            // 图例范围只依据当前显示样本，确保颜色解释与覆盖层一致
            for (int z = sampleStart; z < bakeAsset.Height; z += stride)
            {
                for (int x = sampleStart; x < bakeAsset.Width; x += stride)
                {
                    NavigationGridCellData cell = bakeAsset.GetCell(x + z * bakeAsset.Width);
                    if (!cell.Walkable)
                    {
                        continue;
                    }

                    minimum = Mathf.Min(minimum, cell.TerrainCost);
                    maximum = Mathf.Max(maximum, cell.TerrainCost);
                }
            }

            if (float.IsPositiveInfinity(minimum))
            {
                minimum = 0f;
                maximum = 0f;
            }
        }

        // 所有预览模式都在这里映射颜色
        // 状态色与连续渐变分别处理，减少同一颜色表达不同含义的情况
        private static Color ResolveBucketColor(
            NavigationGridGizmoMode mode,
            int bucketIndex,
            float opacity)
        {
            if (bucketIndex == 0)
            {
                return WithOpacity(_blockedColor, opacity * 0.82f);
            }

            // 连续数据在建 Mesh 时已经量化，这里只把档位还原为对应渐变颜色
            float ratio = ContinuousBucketCount <= 1
                ? 0f
                : (bucketIndex - 1f) / (ContinuousBucketCount - 1f);
            Color color;
            switch (mode)
            {
                case NavigationGridGizmoMode.Clearance:
                    // 安全距离小时用暖色警示，中等转为青绿，空间充足时使用蓝色
                    color = ResolveThreeColorGradient(
                        ratio,
                        new Color32(239, 161, 65, 255),
                        new Color32(57, 183, 157, 255),
                        new Color32(61, 135, 224, 255));
                    break;

                case NavigationGridGizmoMode.Region:
                    color = _regionPalette[(bucketIndex - 1) % _regionPalette.Length];
                    break;

                case NavigationGridGizmoMode.Slope:
                    color = ResolveRiskGradient(ratio);
                    break;

                case NavigationGridGizmoMode.TerrainCost:
                    // 低成本使用冷色，成本越高越偏暖色
                    color = ResolveThreeColorGradient(
                        ratio,
                        new Color32(70, 137, 219, 255),
                        new Color32(224, 185, 73, 255),
                        new Color32(215, 79, 83, 255));
                    break;

                case NavigationGridGizmoMode.AgentOccupancy:
                    color = bucketIndex == 2
                        ? _walkableColor
                        : _warningColor;
                    break;

                default:
                    color = _walkableColor;
                    break;
            }

            return WithOpacity(color, opacity);
        }

        // 风险从绿色经黄色过渡到红色
        // 使用三色分段，让临界安全距离比简单双色渐变更醒目
        private static Color ResolveRiskGradient(float ratio)
        {
            Color safe = new Color32(61, 174, 117, 255);
            Color warning = new Color32(239, 178, 70, 255);
            Color danger = new Color32(220, 78, 83, 255);
            return ResolveThreeColorGradient(ratio, safe, warning, danger);
        }

        // 三色渐变在中点前后分别插值；输入先限制到 0 到 1
        private static Color ResolveThreeColorGradient(
            float ratio,
            Color low,
            Color middle,
            Color high)
        {
            // 指定中间色可避免两端直接插值产生灰暗、不易辨认的中段
            return ratio <= 0.5f
                ? Color.Lerp(low, middle, ratio * 2f)
                : Color.Lerp(middle, high, (ratio - 0.5f) * 2f);
        }

        // 透明度只修改覆盖层 Alpha，不改变颜色本身的含义
        private static Color WithOpacity(Color color, float opacity)
        {
            color.a = Mathf.Clamp(opacity, 0.05f, 1f);
            return color;
        }

        // 图例色块和文字采用固定宽度，Inspector 改变尺寸时布局不会跳动
        private static void DrawLegendItem(string label, Color color)
        {
            Rect swatch = GUILayoutUtility.GetRect(
                12f,
                12f,
                GUILayout.Width(12f),
                GUILayout.Height(12f));
            EditorGUI.DrawRect(swatch, color);
            GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
        }

        // 状态模式按实际状态数建档位，连续模式使用统一量化精度
        // 档位上限直接限制子网格数量和绘制次数
        private static int GetBucketCount(NavigationGridGizmoMode mode)
        {
            switch (mode)
            {
                case NavigationGridGizmoMode.Region:
                    return RegionBucketCount + 1;
                case NavigationGridGizmoMode.AgentOccupancy:
                    return 3;
                case NavigationGridGizmoMode.Clearance:
                case NavigationGridGizmoMode.Slope:
                case NavigationGridGizmoMode.TerrainCost:
                    return ContinuousBucketCount + 1;
                default:
                    return 2;
            }
        }

        // 将 0 到 1 的连续值量化为离散档位，并保留两端极值
        private static int QuantizeContinuous(float value)
        {
            return Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Clamp01(value) * (ContinuousBucketCount - 1)),
                0,
                ContinuousBucketCount - 1);
        }

        // 用整数向上取整根据最大显示数量反推抽样步长
        private static int CeilDivide(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        // Domain Reload 或程序集重载前释放编辑器生成的 Mesh
        // 清空缓存后不保留已失效的 UnityEngine.Object 引用
        private static void ClearCache()
        {
            foreach (CacheEntry cacheEntry in _cacheByAuthoringId.Values)
            {
                cacheEntry.Dispose();
            }

            _cacheByAuthoringId.Clear();

            if (_overlayMaterial != null)
            {
                Object.DestroyImmediate(_overlayMaterial);
                _overlayMaterial = null;
            }
        }

        private sealed class CacheEntry : IDisposable
        {
            // 缓存键包含资产实例、内容哈希和所有会改变 Mesh 或颜色档位的显示参数
            // 透明度不改变 Mesh，因此在绘制时动态应用，不参与缓存键
            private readonly int _bakeAssetId;
            private readonly string _dataHash;
            private readonly NavigationGridGizmoMode _mode;
            private readonly int _maximumCells;
            private readonly bool _showBlockedCells;
            private readonly float _maximumSlopeDegrees;
            private readonly float _visualizedAgentRadius;
            private readonly float _visualizedAgentMargin;

            public CacheEntry(
                NavigationGridAuthoring authoring,
                NavigationGridBakeAsset bakeAsset,
                Mesh mesh,
                bool[] hasGeometry)
            {
                // 创建缓存时记录完整键，Authoring 后续变化由 Matches 检测
                _bakeAssetId = bakeAsset.GetInstanceID();
                _dataHash = bakeAsset.DataHash;
                _mode = authoring.GizmoMode;
                _maximumCells = authoring.MaximumGizmoCells;
                _showBlockedCells = authoring.ShowBlockedCells;
                _maximumSlopeDegrees = authoring.MaximumSlopeDegrees;
                _visualizedAgentRadius = authoring.VisualizedAgentRadius;
                _visualizedAgentMargin = authoring.VisualizedAgentMargin;
                Mesh = mesh;
                HasGeometry = hasGeometry;
            }

            // CacheEntry 同时持有 Mesh 和颜色档位占用表，两者一起失效
            public Mesh Mesh { get; }

            public bool[] HasGeometry { get; }

            public bool Matches(
                NavigationGridAuthoring authoring,
                NavigationGridBakeAsset bakeAsset)
            {
                // 资产实例不同，即使内容哈希相同也重建，避免对象生命周期交叉
                // DataHash 判断数据变化，其他显示参数判断预览几何和分档变化
                return _bakeAssetId == bakeAsset.GetInstanceID() &&
                       string.Equals(_dataHash, bakeAsset.DataHash, StringComparison.Ordinal) &&
                       _mode == authoring.GizmoMode &&
                       _maximumCells == authoring.MaximumGizmoCells &&
                       _showBlockedCells == authoring.ShowBlockedCells &&
                       Mathf.Approximately(_maximumSlopeDegrees, authoring.MaximumSlopeDegrees) &&
                       Mathf.Approximately(_visualizedAgentRadius, authoring.VisualizedAgentRadius) &&
                       Mathf.Approximately(_visualizedAgentMargin, authoring.VisualizedAgentMargin);
            }

            public void Dispose()
            {
                // Mesh 从未写入 AssetDatabase，可以在编辑器中立即销毁
                if (Mesh != null)
                {
                    Object.DestroyImmediate(Mesh);
                }
            }
        }
    }
}
#endif
