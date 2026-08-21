#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 在编辑器中采样场景物理几何、生成导航网格资产，并检查现有资产是否已经过期
    /// </summary>
    public static class NavigationGridBakeUtility
    {
        private const int MaximumRaycastHits = 128;
        private const int MaximumOverlapHits = 128;
        private const int MaximumCellCount = 4_000_000;
        private const float SampleEpsilon = 0.001f;

        /// <summary>
        /// 采样当前场景并完整更新对应的导航网格资产
        /// </summary>
        /// <param name="authoring">待烘焙的 Grid 配置</param>
        /// <returns>创建或更新后的烘焙资产</returns>
        public static NavigationGridBakeAsset Bake(NavigationGridAuthoring authoring)
        {
            // 先同步 Transform，使物理采样和几何哈希读取同一份场景状态
            // 所有格子和分层数据都在内存中生成完毕后，再一次性替换资产内容
            if (!TryValidateSettings(authoring, out string settingsError))
            {
                throw new InvalidOperationException(settingsError);
            }

            Scene scene = authoring.gameObject.scene;
            string scenePath = scene.path;
            string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
            Physics.SyncTransforms();
            string parameterHash = ComputeParameterHash(authoring);
            string geometryHash = ComputeGeometryHash(authoring);
            int2 dimensions = authoring.GridDimensions;
            int width = dimensions.x;
            int height = dimensions.y;
            NavigationGridCellData[] cells = new NavigationGridCellData[width * height];

            // 物理查询先生成基础格子，随后再计算邻接、安全距离、连通区域和分层数据
            Dictionary<Collider, string> colliderKeys = BuildColliderKeyLookup(authoring);
            SampleCells(authoring, colliderKeys, cells, width, height);
            NavigationGridBakingAlgorithms.BuildConnectivity(
                cells,
                width,
                height,
                authoring.MaximumStepHeight);
            NavigationEuclideanDistanceTransform.Calculate(cells, width, height, authoring.CellSize);
            NavigationGridBakingAlgorithms.AssignClusters(
                cells,
                width,
                height,
                authoring.ClusterSizeInCells);
            int regionCount = NavigationGridBakingAlgorithms.AssignRegions(cells, width, height);

            // 在计算内容哈希和写入资产之前统一量化浮点值，确保两处使用完全相同的数据
            QuantizeCells(cells);
            NavigationGridHierarchyBuildResult hierarchy =
                NavigationGridHierarchyBuilder.Build(
                    cells,
                    width,
                    height,
                    authoring.ClusterSizeInCells,
                    authoring.CellSize);
            var result = new NavigationGridBakeResult
            {
                SourceSceneGuid = sceneGuid,
                SourceScenePath = scenePath,
                GeometryHash = geometryHash,
                ParameterHash = parameterHash,
                ToolVersion = NavigationGridBakeAsset.CurrentToolVersion,
                DataVersion = NavigationGridBakeAsset.CurrentDataVersion,
                WorldBounds = authoring.WorldBounds,
                CellSize = authoring.CellSize,
                BaseAgentRadius = authoring.BaseAgentRadius,
                BaseAgentHeight = authoring.BaseAgentHeight,
                Width = width,
                Height = height,
                ClusterSizeInCells = authoring.ClusterSizeInCells,
                ClusterWidth = hierarchy.ClusterWidth,
                ClusterHeight = hierarchy.ClusterHeight,
                RegionCount = regionCount,
                Cells = cells,
                Clusters = hierarchy.Clusters,
                Portals = hierarchy.Portals,
                PortalNodes = hierarchy.PortalNodes,
                AbstractEdges = hierarchy.AbstractEdges,
                ClusterPortalNodeIndices = hierarchy.ClusterPortalNodeIndices,
            };
            result.DataHash = ComputeDataHash(result);

            NavigationGridBakeAsset bakeAsset = GetOrCreateBakeAsset(authoring);
            Undo.RecordObject(bakeAsset, "Bake Navigation Grid");
            bakeAsset.ApplyBakeResult(result);
            EditorUtility.SetDirty(bakeAsset);

            if (authoring.BakeAsset != bakeAsset)
            {
                Undo.RecordObject(authoring, "Assign Navigation Grid Bake Asset");
                authoring.AssignBakeAsset(bakeAsset);
                EditorUtility.SetDirty(authoring);
                EditorSceneManager.MarkSceneDirty(scene);
            }

            AssetDatabase.SaveAssets();
            return bakeAsset;
        }

        /// <summary>
        /// 检查资产的场景来源、配置、几何和内容是否仍与当前场景一致
        /// </summary>
        /// <param name="authoring">待校验的 Grid 配置</param>
        /// <param name="message">校验结果说明</param>
        /// <returns>资产仍然有效、不需要重新烘焙时返回 true</returns>
        public static bool TryValidateCurrentAsset(
            NavigationGridAuthoring authoring,
            out string message)
        {
            // 依次检查场景来源、版本、配置、几何和资产内容
            // 任一项不一致都要求完整重新烘焙，不尝试局部修补旧资产
            if (!TryValidateSettings(authoring, out message))
            {
                return false;
            }

            NavigationGridBakeAsset bakeAsset = authoring.BakeAsset;
            if (bakeAsset == null)
            {
                message = "缺少 NavigationGridBakeAsset";
                return false;
            }

            if (!bakeAsset.IsUsable)
            {
                message = "Grid 资产为空、结构损坏或数据版本不受支持";
                return false;
            }

            Scene scene = authoring.gameObject.scene;
            string sceneGuid = AssetDatabase.AssetPathToGUID(scene.path);
            if (!string.Equals(bakeAsset.SourceSceneGuid, sceneGuid, StringComparison.Ordinal))
            {
                message = "Grid 资产来源场景与当前场景不一致";
                return false;
            }

            if (!string.Equals(
                    bakeAsset.ToolVersion,
                    NavigationGridBakeAsset.CurrentToolVersion,
                    StringComparison.Ordinal) ||
                bakeAsset.DataVersion != NavigationGridBakeAsset.CurrentDataVersion)
            {
                message = "Grid 资产工具版本或数据版本已过期";
                return false;
            }

            // 配置和场景几何分别比较，用户可以准确知道资产为什么过期
            string parameterHash = ComputeParameterHash(authoring);
            if (!string.Equals(bakeAsset.ParameterHash, parameterHash, StringComparison.Ordinal))
            {
                message = "Authoring 参数已变化，需要重新烘焙";
                return false;
            }

            string geometryHash = ComputeGeometryHash(authoring);
            if (!string.Equals(bakeAsset.GeometryHash, geometryHash, StringComparison.Ordinal))
            {
                message = "场景地面或障碍几何已变化，需要重新烘焙";
                return false;
            }

            string dataHash = ComputeDataHash(bakeAsset);
            if (!string.Equals(bakeAsset.DataHash, dataHash, StringComparison.Ordinal))
            {
                message = "Grid Cell 数据与 Data Hash 不一致";
                return false;
            }

            message = "Grid 数据有效";
            return true;
        }

        /// <summary>
        /// 检查场景配置是否完整，并且能够生成可重复验证的烘焙结果
        /// </summary>
        /// <param name="authoring">待检查的 Grid 配置</param>
        /// <param name="message">配置检查结果</param>
        /// <returns>配置和场景状态允许烘焙时返回 true</returns>
        public static bool TryValidateSettings(
            NavigationGridAuthoring authoring,
            out string message)
        {
            // 配置检查既防止无效结果，也限制极端尺寸造成的内存占用
            // 未保存场景没有固定 GUID 和对象路径，无法可靠判断资产是否过期，因此不允许烘焙
            if (authoring == null)
            {
                message = "缺少 NavigationGridAuthoring";
                return false;
            }

            Scene scene = authoring.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrWhiteSpace(scene.path))
            {
                message = "必须先保存并加载 Authoring 所在场景";
                return false;
            }

            if (scene.isDirty)
            {
                message = "场景存在未保存修改 请先保存再烘焙或校验";
                return false;
            }

            if (authoring.GroundLayers.value == 0)
            {
                message = "Ground Layers 不能为空";
                return false;
            }

            if (authoring.ObstacleLayers.value == 0)
            {
                message = "Obstacle Layers 不能为空";
                return false;
            }

            if ((authoring.GroundLayers.value & authoring.ObstacleLayers.value) != 0)
            {
                message = "Ground Layers 与 Obstacle Layers 不能重叠";
                return false;
            }

            Bounds configuredBounds = authoring.ConfiguredWorldBounds;
            if (authoring.CellSize <= 0f ||
                configuredBounds.size.x < authoring.CellSize ||
                configuredBounds.size.z < authoring.CellSize ||
                configuredBounds.size.y <= 0f)
            {
                message = "World Bounds 必须至少容纳一个完整 Cell";
                return false;
            }

            // 使用 long 计算格子总数，防止宽高相乘时先发生整数溢出
            int2 dimensions = authoring.GridDimensions;
            long cellCount = (long)dimensions.x * dimensions.y;
            if (cellCount <= 0 || cellCount > MaximumCellCount)
            {
                message = $"Cell 数量必须在 1 到 {MaximumCellCount} 之间";
                return false;
            }

            if (authoring.BaseAgentRadius <= 0f ||
                authoring.BaseAgentHeight < authoring.BaseAgentRadius * 2f)
            {
                message = "Base Agent Height 必须大于等于直径";
                return false;
            }

            message = "Authoring 参数有效";
            return true;
        }

        /// <summary>
        /// 根据所有影响烘焙结果的配置计算参数哈希
        /// </summary>
        /// <param name="authoring">Grid 配置</param>
        /// <returns>32 位十六进制文本哈希</returns>
        public static string ComputeParameterHash(NavigationGridAuthoring authoring)
        {
            // 字段顺序是参数哈希格式的一部分，修改顺序会让已有资产被判定为过期
            // 地形成本规则按配置顺序匹配，不能为了计算哈希而重新排序
            using var writer = new NavigationGridHashWriter();
            writer.Append(NavigationGridBakeAsset.CurrentToolVersion);
            writer.Append(NavigationGridBakeAsset.CurrentDataVersion);
            writer.Append(authoring.ConfiguredWorldBounds);
            writer.Append(authoring.WorldBounds);
            writer.Append(authoring.CellSize);
            writer.Append(authoring.GroundLayers.value);
            writer.Append(authoring.ObstacleLayers.value);
            writer.Append(authoring.MaximumSlopeDegrees);
            writer.Append(authoring.MaximumStepHeight);
            writer.Append(authoring.BaseAgentRadius);
            writer.Append(authoring.BaseAgentHeight);
            writer.Append(authoring.ClusterSizeInCells);
            writer.Append(authoring.DefaultTerrainCost);

            writer.Append(authoring.TerrainCostRuleCount);
            for (int i = 0; i < authoring.TerrainCostRuleCount; i++)
            {
                NavigationTerrainCostRule terrainCostRule = authoring.GetTerrainCostRule(i);
                writer.Append(terrainCostRule.GroundLayers.value);
                writer.Append(terrainCostRule.Cost);
            }

            return writer.FinishHash128();
        }

        /// <summary>
        /// 根据烘焙范围内的 Collider 和外部网格资源计算场景几何哈希
        /// </summary>
        /// <param name="authoring">限定场景、Layer 和 Bounds 的 Grid 配置</param>
        /// <returns>32 位十六进制文本哈希</returns>
        public static string ComputeGeometryHash(NavigationGridAuthoring authoring)
        {
            // 先按可跨会话识别的路径排序 Collider，再加入 Mesh、TerrainData 等依赖资源的摘要
            // 边界和物理采样都读取同步后的同一份 Physics 状态
            Physics.SyncTransforms();
            List<Collider> colliders = CollectRelevantColliders(authoring);
            var records = new List<NavigationGridColliderRecord>(colliders.Count);
            for (int i = 0; i < colliders.Count; i++)
            {
                Collider collider = colliders[i];
                records.Add(new NavigationGridColliderRecord(
                    collider,
                    GetStableObjectKey(collider)));
            }

            // 排序使用场景或资产路径，不使用重启编辑器后会变化的 InstanceId
            records.Sort((left, right) =>
                string.CompareOrdinal(left.StableKey, right.StableKey));

            using var writer = new NavigationGridHashWriter();
            writer.Append(NavigationGridBakeAsset.CurrentToolVersion);
            writer.Append(authoring.gameObject.scene.path);
            writer.Append(records.Count);

            for (int i = 0; i < records.Count; i++)
            {
                Collider collider = records[i].Collider;
                writer.Append(records[i].StableKey);
                writer.Append(collider.GetType().FullName ?? collider.GetType().Name);
                writer.Append(collider.gameObject.layer);
                writer.Append(collider.enabled);
                writer.Append(collider.isTrigger);
                writer.Append(collider.transform.localToWorldMatrix);
                writer.Append(collider.bounds);
                writer.Append(EditorJsonUtility.ToJson(collider, false));
                AppendColliderDependencies(writer, collider);
            }

            return writer.FinishHash128();
        }

        /// <summary>
        /// 将格子索引转换为该格子中心在烘焙地面上的世界坐标
        /// </summary>
        /// <param name="bakeAsset">包含尺寸和高度的 Grid 资产</param>
        /// <param name="index">Cell 行主序索引</param>
        /// <param name="verticalOffset">用于 Gizmo 的垂直偏移</param>
        /// <returns>目标格子中心的世界坐标</returns>
        public static Vector3 GetCellCenter(
            NavigationGridBakeAsset bakeAsset,
            int index,
            float verticalOffset = 0f)
        {
            int x = index % bakeAsset.Width;
            int z = index / bakeAsset.Width;
            NavigationGridCellData cell = bakeAsset.GetCell(index);
            Bounds bounds = bakeAsset.WorldBounds;
            return new Vector3(
                bounds.min.x + (x + 0.5f) * bakeAsset.CellSize,
                cell.Height + verticalOffset,
                bounds.min.z + (z + 0.5f) * bakeAsset.CellSize);
        }

        // 地面支撑、坡度和角色空间共同决定格子能否站立；邻接和安全距离稍后统一计算
        private static void SampleCells(
            NavigationGridAuthoring authoring,
            Dictionary<Collider, string> colliderKeys,
            NavigationGridCellData[] cells,
            int width,
            int height)
        {
            Bounds bounds = authoring.WorldBounds;
            float rayOriginHeight = bounds.max.y + SampleEpsilon;
            float rayDistance = bounds.size.y + SampleEpsilon * 2f;
            var raycastHits = new RaycastHit[MaximumRaycastHits];
            var overlapHits = new Collider[MaximumOverlapHits];

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = x + z * width;
                    Vector3 rayOrigin = new Vector3(
                        bounds.min.x + (x + 0.5f) * authoring.CellSize,
                        rayOriginHeight,
                        bounds.min.z + (z + 0.5f) * authoring.CellSize);

                    NavigationGridCellData cell = new NavigationGridCellData
                    {
                        Height = bounds.min.y,
                        SurfaceNormal = Vector3.up,
                        SlopeDegrees = 90f,
                        TerrainCost = authoring.DefaultTerrainCost,
                        Clearance = 0f,
                        RegionId = 0,
                        ClusterId = 0,
                        NeighborMask = NavigationNeighborMask.None,
                        Walkable = false,
                    };

                    if (TryFindGround(
                            authoring,
                            colliderKeys,
                            rayOrigin,
                            rayDistance,
                            raycastHits,
                            out RaycastHit groundHit))
                    {
                        float slopeDegrees = Vector3.Angle(groundHit.normal, Vector3.up);
                        bool supported = HasBaseAgentGroundSupport(
                            authoring,
                            colliderKeys,
                            groundHit,
                            rayOriginHeight,
                            rayDistance,
                            raycastHits);
                        bool blocked = HasStaticObstacle(
                            authoring,
                            groundHit,
                            overlapHits);

                        cell.Height = groundHit.point.y;
                        cell.SurfaceNormal = groundHit.normal.normalized;
                        cell.SlopeDegrees = slopeDegrees;
                        cell.TerrainCost = authoring.ResolveTerrainCost(
                            groundHit.collider.gameObject.layer);
                        cell.Walkable =
                            slopeDegrees <= authoring.MaximumSlopeDegrees + SampleEpsilon &&
                            supported &&
                            !blocked;
                    }

                    cells[index] = cell;
                }
            }
        }

        // 多个地面命中按距离和对象路径决定优先级，并且只接受当前场景中的对象
        // 命中数达到缓冲区上限时明确失败，避免悄悄丢掉可能更合适的地面
        private static bool TryFindGround(
            NavigationGridAuthoring authoring,
            Dictionary<Collider, string> colliderKeys,
            Vector3 rayOrigin,
            float rayDistance,
            RaycastHit[] hits,
            out RaycastHit groundHit)
        {
            int hitCount = Physics.RaycastNonAlloc(
                rayOrigin,
                Vector3.down,
                hits,
                rayDistance,
                authoring.GroundLayers,
                QueryTriggerInteraction.Ignore);

            if (hitCount >= hits.Length)
            {
                throw new InvalidOperationException(
                    $"Ground Raycast 命中数量达到上限 {hits.Length} 请清理重叠 Collider");
            }

            groundHit = default;
            bool found = false;
            string bestStableKey = string.Empty;
            float bestDistance = float.PositiveInfinity;
            Scene sourceScene = authoring.gameObject.scene;

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit candidate = hits[i];
                Collider collider = candidate.collider;
                if (collider == null ||
                    collider.gameObject.scene != sourceScene ||
                    !colliderKeys.TryGetValue(collider, out string stableKey))
                {
                    continue;
                }

                bool nearer = candidate.distance < bestDistance - SampleEpsilon;
                bool stableTie =
                    Mathf.Abs(candidate.distance - bestDistance) <= SampleEpsilon &&
                    string.CompareOrdinal(stableKey, bestStableKey) < 0;
                if (!found || nearer || stableTie)
                {
                    found = true;
                    bestDistance = candidate.distance;
                    bestStableKey = stableKey;
                    groundHit = candidate;
                }
            }

            return found;
        }

        // 在角色脚底圆周上补充采样，用来排除窄边缘、悬空位置和断裂平台
        // 周边支撑点与中心地面的高度差必须在允许范围内
        private static bool HasBaseAgentGroundSupport(
            NavigationGridAuthoring authoring,
            Dictionary<Collider, string> colliderKeys,
            RaycastHit centerHit,
            float rayOriginHeight,
            float rayDistance,
            RaycastHit[] hits)
        {
            const int supportSampleCount = 16;
            float supportRadius = authoring.BaseAgentRadius * 0.95f;
            float maximumSlopeRadians = authoring.MaximumSlopeDegrees * Mathf.Deg2Rad;
            float slopeHeightAllowance = Mathf.Tan(maximumSlopeRadians) * supportRadius;
            float maximumHeightDifference = Mathf.Max(
                authoring.MaximumStepHeight,
                slopeHeightAllowance) + SampleEpsilon;
            Bounds bounds = authoring.WorldBounds;

            // 即使中心点有地面，脚底周边伸出悬崖或窄平台时也不能算作可站立
            for (int sampleIndex = 0; sampleIndex < supportSampleCount; sampleIndex++)
            {
                float angle = sampleIndex * Mathf.PI * 2f / supportSampleCount;
                Vector3 supportPosition = centerHit.point + new Vector3(
                    Mathf.Cos(angle) * supportRadius,
                    0f,
                    Mathf.Sin(angle) * supportRadius);

                if (supportPosition.x < bounds.min.x + SampleEpsilon ||
                    supportPosition.x > bounds.max.x - SampleEpsilon ||
                    supportPosition.z < bounds.min.z + SampleEpsilon ||
                    supportPosition.z > bounds.max.z - SampleEpsilon)
                {
                    return false;
                }

                Vector3 supportRayOrigin = new Vector3(
                    supportPosition.x,
                    rayOriginHeight,
                    supportPosition.z);
                if (!TryFindGround(
                        authoring,
                        colliderKeys,
                        supportRayOrigin,
                        rayDistance,
                        hits,
                        out RaycastHit supportHit))
                {
                    return false;
                }

                float supportSlope = Vector3.Angle(supportHit.normal, Vector3.up);
                if (supportSlope > authoring.MaximumSlopeDegrees + SampleEpsilon ||
                    Mathf.Abs(supportHit.point.y - centerHit.point.y) > maximumHeightDifference)
                {
                    return false;
                }
            }

            return true;
        }

        // 角色检测体积略微抬离地面，避免将支撑地面本身误判为障碍
        // Trigger 和未配置为障碍的 Layer 不参与占用检查
        private static bool HasStaticObstacle(
            NavigationGridAuthoring authoring,
            RaycastHit groundHit,
            Collider[] overlaps)
        {
            float radius = authoring.BaseAgentRadius;
            Vector3 bottom = groundHit.point + Vector3.up * radius;
            Vector3 top = groundHit.point + Vector3.up *
                Mathf.Max(radius, authoring.BaseAgentHeight - radius);

            int overlapCount = Physics.OverlapCapsuleNonAlloc(
                bottom,
                top,
                radius,
                overlaps,
                authoring.ObstacleLayers,
                QueryTriggerInteraction.Ignore);

            if (overlapCount >= overlaps.Length)
            {
                throw new InvalidOperationException(
                    $"Obstacle Overlap 命中数量达到上限 {overlaps.Length} 请清理重叠 Collider");
            }

            Scene sourceScene = authoring.gameObject.scene;
            for (int i = 0; i < overlapCount; i++)
            {
                Collider collider = overlaps[i];
                if (collider == null ||
                    collider == groundHit.collider ||
                    collider.gameObject.scene != sourceScene ||
                    !collider.enabled ||
                    !collider.gameObject.activeInHierarchy ||
                    collider.isTrigger)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        // 当前场景已有自己的资产时原地更新，已有引用不会失效
        // 复制场景若仍引用原场景资产，则创建独立副本，避免覆盖来源场景的数据
        private static NavigationGridBakeAsset GetOrCreateBakeAsset(
            NavigationGridAuthoring authoring)
        {
            if (authoring.BakeAsset != null)
            {
                string existingPath = AssetDatabase.GetAssetPath(authoring.BakeAsset);
                if (string.IsNullOrWhiteSpace(existingPath))
                {
                    throw new InvalidOperationException("Bake Asset 必须是项目内持久化资产");
                }

                string currentSceneGuid = AssetDatabase.AssetPathToGUID(
                    authoring.gameObject.scene.path);
                bool belongsToCurrentScene =
                    string.IsNullOrWhiteSpace(authoring.BakeAsset.SourceSceneGuid) ||
                    string.Equals(
                        authoring.BakeAsset.SourceSceneGuid,
                        currentSceneGuid,
                        StringComparison.Ordinal);
                if (belongsToCurrentScene)
                {
                    return authoring.BakeAsset;
                }

                // Unity 复制场景时会保留原 ScriptableObject 引用，因此新场景要换成自己的资产
                return CreateBakeAsset(authoring);
            }

            return CreateBakeAsset(authoring);
        }

        // 输出目录和文件名前缀遵循项目约定
        // 使用唯一资产路径，避免同名场景或历史资产被无提示覆盖
        private static NavigationGridBakeAsset CreateBakeAsset(
            NavigationGridAuthoring authoring)
        {
            const string outputFolder = "Assets/SO/Navigation";
            EnsureAssetFolder(outputFolder);
            string sceneName = SanitizeAssetName(authoring.gameObject.scene.name);
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{outputFolder}/SO_NavigationGrid_{sceneName}.asset");
            NavigationGridBakeAsset bakeAsset =
                ScriptableObject.CreateInstance<NavigationGridBakeAsset>();
            AssetDatabase.CreateAsset(bakeAsset, assetPath);
            return bakeAsset;
        }

        // AssetDatabase 需要逐级创建目录；已经存在的层级直接复用
        private static void EnsureAssetFolder(string folderPath)
        {
            string[] segments = folderPath.Split('/');
            string currentPath = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string nextPath = $"{currentPath}/{segments[i]}";
                if (!AssetDatabase.IsValidFolder(nextPath))
                {
                    AssetDatabase.CreateFolder(currentPath, segments[i]);
                }

                currentPath = nextPath;
            }
        }

        // 场景名用于文件名之前先移除非法字符
        // 清理后为空时使用固定备用名称，确保仍能生成可定位资产
        private static string SanitizeAssetName(string value)
        {
            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                builder.Append(Array.IndexOf(invalidCharacters, character) >= 0 ? '_' : character);
            }

            return builder.ToString();
        }

        // 物理采样会频繁查询对象身份，因此用字典缓存对象路径
        // 每次射线命中便无需重复访问 AssetDatabase 和 Hierarchy
        private static Dictionary<Collider, string> BuildColliderKeyLookup(
            NavigationGridAuthoring authoring)
        {
            List<Collider> colliders = CollectRelevantColliders(authoring);
            var result = new Dictionary<Collider, string>(colliders.Count);
            for (int i = 0; i < colliders.Count; i++)
            {
                result[colliders[i]] = GetStableObjectKey(colliders[i]);
            }

            return result;
        }

        // 这里只按场景、Layer、范围和启用状态筛选，之后再按对象路径排序
        // Trigger 和无效 Collider 都不参与几何哈希或物理采样
        private static List<Collider> CollectRelevantColliders(
            NavigationGridAuthoring authoring)
        {
            Collider[] allColliders = Object.FindObjectsByType<Collider>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            var result = new List<Collider>();
            Scene sourceScene = authoring.gameObject.scene;
            Bounds worldBounds = authoring.WorldBounds;
            int relevantLayers =
                authoring.GroundLayers.value |
                authoring.ObstacleLayers.value;

            for (int i = 0; i < allColliders.Length; i++)
            {
                Collider collider = allColliders[i];
                int layerBit = 1 << collider.gameObject.layer;
                if (collider.gameObject.scene != sourceScene ||
                    !collider.enabled ||
                    !collider.gameObject.activeInHierarchy ||
                    collider.isTrigger ||
                    (relevantLayers & layerBit) == 0 ||
                    !worldBounds.Intersects(collider.bounds))
                {
                    continue;
                }

                result.Add(collider);
            }

            return result;
        }

        // 资产对象使用项目路径，场景组件使用场景路径加 Hierarchy 路径
        // 这些身份不能依赖重启编辑器后会变化的 InstanceId
        private static string GetStableObjectKey(Object target)
        {
            GlobalObjectId globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(target);
            if ((int)globalObjectId.identifierType != 0)
            {
                return globalObjectId.ToString();
            }

            if (target is Component component)
            {
                throw new InvalidOperationException(
                    $"参与 Grid 烘焙的 Collider 必须属于已保存场景: {BuildHierarchyFallbackKey(component)}");
            }

            string assetPath = AssetDatabase.GetAssetPath(target);
            return $"{target.GetType().FullName}:{assetPath}:{target.name}";
        }

        // Hierarchy 路径包含同名兄弟对象的索引，避免名称相同的对象混淆
        // 未保存场景无法生成可跨会话识别的路径，已在上层配置检查中拒绝
        private static string BuildHierarchyFallbackKey(Component component)
        {
            var segments = new List<string>();
            Transform current = component.transform;
            while (current != null)
            {
                segments.Add($"{current.GetSiblingIndex()}:{current.name}");
                current = current.parent;
            }

            segments.Reverse();
            Component[] sameTypeComponents = component.GetComponents(component.GetType());
            int componentIndex = Array.IndexOf(sameTypeComponents, component);
            return $"{component.gameObject.scene.path}/{string.Join("/", segments)}#{component.GetType().FullName}:{componentIndex}";
        }

        // Collider 自身的序列化字段不能反映 Mesh 或 TerrainData 内容变化
        // 因此还要加入外部依赖的哈希，使资源修改能够触发重新烘焙
        private static void AppendColliderDependencies(
            NavigationGridHashWriter writer,
            Collider collider)
        {
            if (collider is MeshCollider meshCollider)
            {
                AppendObjectDependency(writer, meshCollider.sharedMesh);
            }
            else if (collider is TerrainCollider terrainCollider)
            {
                AppendObjectDependency(writer, terrainCollider.terrainData);
            }
        }

        // 没有依赖资源时写入明确占位值，保持哈希字段顺序不变
        // 有持久化资源时同时写入资产路径和依赖哈希
        private static void AppendObjectDependency(
            NavigationGridHashWriter writer,
            Object dependency)
        {
            if (dependency == null)
            {
                writer.Append(string.Empty);
                writer.Append(string.Empty);
                return;
            }

            writer.Append(GetStableObjectKey(dependency));
            string path = AssetDatabase.GetAssetPath(dependency);
            writer.Append(path);
            writer.Append(string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : AssetDatabase.GetAssetDependencyHash(path).ToString());
        }

        // 物理采样结果先量化，再进入内容哈希和运行时 Blob
        // 固定精度可以消除没有业务意义的浮点尾差，提高不同机器重复烘焙的一致性
        private static void QuantizeCells(NavigationGridCellData[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                NavigationGridCellData cell = cells[i];
                cell.Height = Quantize(cell.Height);
                cell.SurfaceNormal = new Vector3(
                    Quantize(cell.SurfaceNormal.x),
                    Quantize(cell.SurfaceNormal.y),
                    Quantize(cell.SurfaceNormal.z));
                cell.SlopeDegrees = Quantize(cell.SlopeDegrees);
                cell.TerrainCost = Quantize(cell.TerrainCost);
                cell.Clearance = Quantize(cell.Clearance);
                cells[i] = cell;
            }
        }

        // 中点采用远离零的舍入方式，使正负值使用对称规则
        private static float Quantize(float value)
        {
            return Mathf.Round(value * 10_000f) / 10_000f;
        }

        // 内存结果和持久化资产使用完全相同的字段顺序计算内容哈希
        // 哈希覆盖网格元数据、所有格子字段和分层寻路数据
        private static string ComputeDataHash(NavigationGridBakeResult result)
        {
            using var writer = new NavigationGridHashWriter();
            AppendDataHeader(
                writer,
                result.SourceSceneGuid,
                result.GeometryHash,
                result.ParameterHash,
                result.ToolVersion,
                result.DataVersion,
                result.WorldBounds,
                result.CellSize,
                result.BaseAgentRadius,
                result.BaseAgentHeight,
                result.Width,
                result.Height,
                result.ClusterSizeInCells,
                result.ClusterWidth,
                result.ClusterHeight,
                result.RegionCount,
                result.Cells.Length,
                result.Clusters.Length,
                result.Portals.Length,
                result.PortalNodes.Length,
                result.AbstractEdges.Length,
                result.ClusterPortalNodeIndices.Length);
            AppendCells(writer, result.Cells.Length, index => result.Cells[index]);
            AppendHierarchy(
                writer,
                result.Clusters.Length,
                index => result.Clusters[index],
                result.Portals.Length,
                index => result.Portals[index],
                result.PortalNodes.Length,
                index => result.PortalNodes[index],
                result.AbstractEdges.Length,
                index => result.AbstractEdges[index],
                result.ClusterPortalNodeIndices.Length,
                index => result.ClusterPortalNodeIndices[index]);
            return writer.FinishHash128();
        }

        // 从资产重新计算内容哈希，可发现手工修改或序列化损坏
        // 该路径必须与内存结果采用完全相同的字节顺序
        private static string ComputeDataHash(NavigationGridBakeAsset bakeAsset)
        {
            using var writer = new NavigationGridHashWriter();
            AppendDataHeader(
                writer,
                bakeAsset.SourceSceneGuid,
                bakeAsset.GeometryHash,
                bakeAsset.ParameterHash,
                bakeAsset.ToolVersion,
                bakeAsset.DataVersion,
                bakeAsset.WorldBounds,
                bakeAsset.CellSize,
                bakeAsset.BaseAgentRadius,
                bakeAsset.BaseAgentHeight,
                bakeAsset.Width,
                bakeAsset.Height,
                bakeAsset.ClusterSizeInCells,
                bakeAsset.ClusterWidth,
                bakeAsset.ClusterHeight,
                bakeAsset.RegionCount,
                bakeAsset.CellCount,
                bakeAsset.ClusterCount,
                bakeAsset.PortalCount,
                bakeAsset.PortalNodeCount,
                bakeAsset.AbstractEdgeCount,
                bakeAsset.ClusterPortalNodeIndexCount);
            AppendCells(writer, bakeAsset.CellCount, bakeAsset.GetCell);
            AppendHierarchy(
                writer,
                bakeAsset.ClusterCount,
                bakeAsset.GetCluster,
                bakeAsset.PortalCount,
                bakeAsset.GetPortal,
                bakeAsset.PortalNodeCount,
                bakeAsset.GetPortalNode,
                bakeAsset.AbstractEdgeCount,
                bakeAsset.GetAbstractEdge,
                bakeAsset.ClusterPortalNodeIndexCount,
                bakeAsset.GetClusterPortalNodeIndex);
            return writer.FinishHash128();
        }

        // 元数据字段顺序属于内容哈希格式的一部分
        // 新增、删除或重排字段时，必须提升 DataVersion 并同步更新两条哈希计算路径
        private static void AppendDataHeader(
            NavigationGridHashWriter writer,
            string sceneGuid,
            string geometryHash,
            string parameterHash,
            string toolVersion,
            int dataVersion,
            Bounds bounds,
            float cellSize,
            float baseAgentRadius,
            float baseAgentHeight,
            int width,
            int height,
            int clusterSizeInCells,
            int clusterWidth,
            int clusterHeight,
            int regionCount,
            int cellCount,
            int clusterCount,
            int portalCount,
            int portalNodeCount,
            int abstractEdgeCount,
            int clusterPortalNodeIndexCount)
        {
            writer.Append(sceneGuid);
            writer.Append(geometryHash);
            writer.Append(parameterHash);
            writer.Append(toolVersion);
            writer.Append(dataVersion);
            writer.Append(bounds);
            writer.Append(cellSize);
            writer.Append(baseAgentRadius);
            writer.Append(baseAgentHeight);
            writer.Append(width);
            writer.Append(height);
            writer.Append(clusterSizeInCells);
            writer.Append(clusterWidth);
            writer.Append(clusterHeight);
            writer.Append(regionCount);
            writer.Append(cellCount);
            writer.Append(clusterCount);
            writer.Append(portalCount);
            writer.Append(portalNodeCount);
            writer.Append(abstractEdgeCount);
            writer.Append(clusterPortalNodeIndexCount);
        }

        // 格子按行写入，顺序变化会产生不同内容哈希
        // 通过委托让内存数组和 ScriptableObject 使用同一读取顺序
        private static void AppendCells(
            NavigationGridHashWriter writer,
            int cellCount,
            Func<int, NavigationGridCellData> getCell)
        {
            for (int i = 0; i < cellCount; i++)
            {
                NavigationGridCellData cell = getCell(i);
                writer.Append(cell.Height);
                writer.Append(cell.SurfaceNormal);
                writer.Append(cell.SlopeDegrees);
                writer.Append(cell.TerrainCost);
                writer.Append(cell.Clearance);
                writer.Append(cell.RegionId);
                writer.Append(cell.ClusterId);
                writer.Append((byte)cell.NeighborMask);
                writer.Append(cell.Walkable);
            }
        }

        private static void AppendHierarchy(
            NavigationGridHashWriter writer,
            int clusterCount,
            Func<int, NavigationGridClusterData> getCluster,
            int portalCount,
            Func<int, NavigationGridPortalData> getPortal,
            int portalNodeCount,
            Func<int, NavigationGridPortalNodeData> getPortalNode,
            int abstractEdgeCount,
            Func<int, NavigationGridAbstractEdgeData> getAbstractEdge,
            int clusterPortalNodeIndexCount,
            Func<int, int> getClusterPortalNodeIndex)
        {
            for (int index = 0; index < clusterCount; index++)
            {
                NavigationGridClusterData cluster = getCluster(index);
                writer.Append(cluster.MinimumX);
                writer.Append(cluster.MinimumZ);
                writer.Append(cluster.MaximumXExclusive);
                writer.Append(cluster.MaximumZExclusive);
                writer.Append(cluster.PortalNodeOffset);
                writer.Append(cluster.PortalNodeCount);
            }

            for (int index = 0; index < portalCount; index++)
            {
                NavigationGridPortalData portal = getPortal(index);
                writer.Append(portal.ClusterA);
                writer.Append(portal.ClusterB);
                writer.Append(portal.RegionId);
                writer.Append(portal.FirstCellA);
                writer.Append(portal.LastCellA);
                writer.Append(portal.FirstCellB);
                writer.Append(portal.LastCellB);
                writer.Append(portal.RepresentativeCellA);
                writer.Append(portal.RepresentativeCellB);
                writer.Append(portal.MinimumClearance);
                writer.Append(portal.StaticCostAtoB);
                writer.Append(portal.StaticCostBtoA);
            }

            for (int index = 0; index < portalNodeCount; index++)
            {
                NavigationGridPortalNodeData node = getPortalNode(index);
                writer.Append(node.PortalIndex);
                writer.Append(node.ClusterId);
                writer.Append(node.CellIndex);
                writer.Append(node.EdgeOffset);
                writer.Append(node.EdgeCount);
            }

            for (int index = 0; index < abstractEdgeCount; index++)
            {
                NavigationGridAbstractEdgeData edge = getAbstractEdge(index);
                writer.Append(edge.ToNodeIndex);
                writer.Append(edge.StaticCost);
                writer.Append(edge.MinimumClearance);
                writer.Append(edge.CrossesPortal);
            }

            for (int index = 0; index < clusterPortalNodeIndexCount; index++)
            {
                writer.Append(getClusterPortalNodeIndex(index));
            }
        }

        private readonly struct NavigationGridColliderRecord
        {
            // 将 Collider 与对象路径配对后，排序就不再依赖 Unity 返回对象的顺序
            public NavigationGridColliderRecord(Collider collider, string stableKey)
            {
                Collider = collider;
                StableKey = stableKey;
            }

            public Collider Collider { get; }
            public string StableKey { get; }
        }
    }

    internal sealed class NavigationGridHashWriter : IDisposable
    {
        // 所有值按固定顺序写入；字符串以 UTF-8 编码并带长度前缀
        // 调用 Finish 后不再允许写入，防止哈希意外包含额外字段
        private readonly MemoryStream _stream = new MemoryStream();
        private readonly BinaryWriter _writer;
        private bool _finished;

        public NavigationGridHashWriter()
        {
            // BinaryWriter 释放时保持底层流打开，随后从流起点计算哈希
            _writer = new BinaryWriter(_stream, Encoding.UTF8, true);
        }

        public void Append(string value)
        {
            // 长度前缀用于区分连续字符串，null 按空字符串处理
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
        }

        public void Append(bool value)
        {
            _writer.Write(value);
        }

        public void Append(byte value)
        {
            _writer.Write(value);
        }

        public void Append(int value)
        {
            _writer.Write(value);
        }

        public void Append(float value)
        {
            _writer.Write(value);
        }

        public void Append(Vector3 value)
        {
            // 向量始终按 X、Y、Z 顺序写入
            Append(value.x);
            Append(value.y);
            Append(value.z);
        }

        public void Append(Bounds value)
        {
            // Bounds 写入中心和尺寸，与 Unity 的序列化含义一致
            Append(value.center);
            Append(value.size);
        }

        public void Append(Matrix4x4 value)
        {
            // 矩阵按固定行列顺序写入，不依赖平台内存布局
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    Append(value[row, column]);
                }
            }
        }

        public string FinishHash128()
        {
            // 使用 SHA-256 计算摘要，并截取前 16 字节形成 128 位文本
            // 该哈希只用于检测内容变化，不用作安全签名
            if (_finished)
            {
                throw new InvalidOperationException("Hash writer can only be finished once");
            }

            _finished = true;
            _writer.Flush();
            _stream.Position = 0;
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(_stream);
            var builder = new StringBuilder(32);
            for (int i = 0; i < 16; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        public void Dispose()
        {
            // Writer 和底层内存流都只由当前哈希构建器持有
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
#endif
