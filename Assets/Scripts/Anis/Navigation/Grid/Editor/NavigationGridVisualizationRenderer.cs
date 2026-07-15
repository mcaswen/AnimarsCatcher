#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AnimarsCatcher.Animars.Navigation.Grid.Editor
{
    /// <summary>
    /// 将烘焙 Cell 批量生成为 Scene 视图表面覆盖层
    /// </summary>
    [InitializeOnLoad]
    internal static class NavigationGridVisualizationRenderer
    {
        private const int ContinuousBucketCount = 16;
        private const int RegionBucketCount = 16;

        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");

        // 阻挡与可行走使用跨模式稳定语义色 降低切换视图时的认知成本
        private static readonly Color BlockedColor = new Color32(216, 74, 88, 255);
        private static readonly Color WalkableColor = new Color32(59, 164, 114, 255);
        private static readonly Color WarningColor = new Color32(239, 171, 67, 255);
        // Region 使用固定分类色板 避免 HSV 随机色出现亮度失控或相邻颜色过近
        private static readonly Color[] RegionPalette =
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

        // 每个 Authoring 只保留一份覆盖层 Mesh 参数或数据变化时整体替换
        private static readonly Dictionary<int, CacheEntry> CacheByAuthoringId = new();
        private static Material _overlayMaterial;

        static NavigationGridVisualizationRenderer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ClearCache;
            EditorApplication.quitting += ClearCache;
            EditorApplication.hierarchyChanged += ClearCache;
        }

        /// <summary>
        /// 绘制指定 Authoring 的缓存覆盖层
        /// </summary>
        /// <param name="authoring">提供显示参数的 Grid Authoring</param>
        /// <param name="bakeAsset">提供烘焙 Cell 的 Grid 资产</param>
        public static void Draw(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset)
        {
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

            // 显式材质颜色不依赖 Gizmos 全局状态 避免不同 SRP Scene 视图把子网格显示成同一颜色
            for (int bucketIndex = 0; bucketIndex < cacheEntry.HasGeometry.Length; bucketIndex++)
            {
                if (!cacheEntry.HasGeometry[bucketIndex])
                {
                    continue;
                }

                overlayMaterial.SetColor(
                    ColorPropertyId,
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
        /// 在 Inspector 中绘制当前模式的颜色图例
        /// </summary>
        /// <param name="mode">当前覆盖层显示模式</param>
        public static void DrawLegend(NavigationGridGizmoMode mode)
        {
            if (mode == NavigationGridGizmoMode.Disabled)
            {
                return;
            }

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("图例", GUILayout.Width(30f));
                DrawLegendItem("阻挡", ResolveBucketColor(mode, 0, 1f));

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
        /// 计算二维等距抽样步长并保证样本数不超过显示上限
        /// </summary>
        /// <param name="width">Grid 宽度</param>
        /// <param name="height">Grid 高度</param>
        /// <param name="maximumCells">允许显示的最大 Cell 数量</param>
        /// <returns>大于等于一的二维抽样步长</returns>
        public static int GetSampleStride(int width, int height, int maximumCells)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            maximumCells = Mathf.Max(1, maximumCells);

            int stride = 1;
            while ((long)CeilDivide(width, stride) * CeilDivide(height, stride) > maximumCells)
            {
                stride++;
            }

            return stride;
        }

        private static CacheEntry GetOrBuildCache(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset)
        {
            int authoringId = authoring.GetInstanceID();
            if (CacheByAuthoringId.TryGetValue(authoringId, out CacheEntry current) &&
                current.Matches(authoring, bakeAsset))
            {
                return current;
            }

            current?.Dispose();
            CacheEntry replacement = BuildCache(authoring, bakeAsset);
            CacheByAuthoringId[authoringId] = replacement;
            return replacement;
        }

        private static CacheEntry BuildCache(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset)
        {
            int bucketCount = GetBucketCount(authoring.GizmoMode);
            var vertices = new List<Vector3>();
            var vertexColors = new List<Color32>();
            // 同一颜色桶写入一个子网格 绘制时只需切换材质颜色而不逐 Cell 提交
            var trianglesByBucket = new List<int>[bucketCount];
            for (int bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
            {
                trianglesByBucket[bucketIndex] = new List<int>();
            }

            int stride = GetSampleStride(
                bakeAsset.Width,
                bakeAsset.Height,
                authoring.MaximumGizmoCells);
            // 从每个二维步长块的中心取样 避免行主序抽样形成条纹
            int sampleStart = stride / 2;
            ResolveTerrainCostRange(
                bakeAsset,
                stride,
                sampleStart,
                out float minimumTerrainCost,
                out float maximumTerrainCost);

            float halfCellSize = bakeAsset.CellSize * 0.47f;
            float verticalOffset = Mathf.Clamp(bakeAsset.CellSize * 0.03f, 0.015f, 0.1f);
            Bounds bounds = bakeAsset.WorldBounds;

            // 顶点直接使用世界坐标 缓存不依赖 Authoring Transform 且与烘焙 Bounds 完全一致
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
                    // Sprite Shader 会把材质色乘以顶点色 显式白色可以完整保留颜色桶结果
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

            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return new CacheEntry(authoring, bakeAsset, mesh, hasGeometry);
        }

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

            // 内置 Sprite Shader 提供无光照透明混合 不受 Scene 灯光和材质预览模式影响
            _overlayMaterial = new Material(shader)
            {
                name = "Navigation Grid Visualization Material",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _overlayMaterial.mainTexture = Texture2D.whiteTexture;
            return _overlayMaterial;
        }

        private static int ResolveBucketIndex(
            NavigationGridAuthoring authoring,
            NavigationGridBakeAsset bakeAsset,
            int cellIndex,
            NavigationGridCellData cell,
            float minimumTerrainCost,
            float maximumTerrainCost)
        {
            // 零号桶统一表示静态阻挡 其余桶由当前模式解释
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

        private static void ResolveTerrainCostRange(
            NavigationGridBakeAsset bakeAsset,
            int stride,
            int sampleStart,
            out float minimum,
            out float maximum)
        {
            minimum = float.PositiveInfinity;
            maximum = float.NegativeInfinity;

            // 成本范围只统计实际显示样本 保证颜色图例与当前覆盖层一致
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

        private static Color ResolveBucketColor(
            NavigationGridGizmoMode mode,
            int bucketIndex,
            float opacity)
        {
            if (bucketIndex == 0)
            {
                return WithOpacity(BlockedColor, opacity * 0.82f);
            }

            // 连续数据已经在建网格时量化为稳定色桶 此处只还原对应梯度颜色
            float ratio = ContinuousBucketCount <= 1
                ? 0f
                : (bucketIndex - 1f) / (ContinuousBucketCount - 1f);
            Color color;
            switch (mode)
            {
                case NavigationGridGizmoMode.Clearance:
                    // 低余量使用暖色提醒风险 中段转为青绿 高余量使用稳定蓝色
                    color = ResolveThreeColorGradient(
                        ratio,
                        new Color32(239, 161, 65, 255),
                        new Color32(57, 183, 157, 255),
                        new Color32(61, 135, 224, 255));
                    break;

                case NavigationGridGizmoMode.Region:
                    color = RegionPalette[(bucketIndex - 1) % RegionPalette.Length];
                    break;

                case NavigationGridGizmoMode.Slope:
                    color = ResolveRiskGradient(ratio);
                    break;

                case NavigationGridGizmoMode.TerrainCost:
                    // 低成本使用冷色 高成本逐步转暖以保持风险方向一致
                    color = ResolveThreeColorGradient(
                        ratio,
                        new Color32(70, 137, 219, 255),
                        new Color32(224, 185, 73, 255),
                        new Color32(215, 79, 83, 255));
                    break;

                case NavigationGridGizmoMode.AgentOccupancy:
                    color = bucketIndex == 2
                        ? WalkableColor
                        : WarningColor;
                    break;

                default:
                    color = WalkableColor;
                    break;
            }

            return WithOpacity(color, opacity);
        }

        private static Color ResolveRiskGradient(float ratio)
        {
            Color safe = new Color32(61, 174, 117, 255);
            Color warning = new Color32(239, 178, 70, 255);
            Color danger = new Color32(220, 78, 83, 255);
            return ResolveThreeColorGradient(ratio, safe, warning, danger);
        }

        private static Color ResolveThreeColorGradient(
            float ratio,
            Color low,
            Color middle,
            Color high)
        {
            // 显式中间色避免两端颜色直接插值产生灰暗且缺少层次的中段
            return ratio <= 0.5f
                ? Color.Lerp(low, middle, ratio * 2f)
                : Color.Lerp(middle, high, (ratio - 0.5f) * 2f);
        }

        private static Color WithOpacity(Color color, float opacity)
        {
            color.a = Mathf.Clamp(opacity, 0.05f, 1f);
            return color;
        }

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

        private static int QuantizeContinuous(float value)
        {
            return Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Clamp01(value) * (ContinuousBucketCount - 1)),
                0,
                ContinuousBucketCount - 1);
        }

        private static int CeilDivide(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private static void ClearCache()
        {
            foreach (CacheEntry cacheEntry in CacheByAuthoringId.Values)
            {
                cacheEntry.Dispose();
            }

            CacheByAuthoringId.Clear();

            if (_overlayMaterial != null)
            {
                Object.DestroyImmediate(_overlayMaterial);
                _overlayMaterial = null;
            }
        }

        private sealed class CacheEntry : IDisposable
        {
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

            public Mesh Mesh { get; }

            public bool[] HasGeometry { get; }

            public bool Matches(
                NavigationGridAuthoring authoring,
                NavigationGridBakeAsset bakeAsset)
            {
                // DataHash 已覆盖 Bounds 尺寸和全部 Cell 内容 显示参数只补充缓存特有条件
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
                if (Mesh != null)
                {
                    Object.DestroyImmediate(Mesh);
                }
            }
        }
    }
}
#endif
