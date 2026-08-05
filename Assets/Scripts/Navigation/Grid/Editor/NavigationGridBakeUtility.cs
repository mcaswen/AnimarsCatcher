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
    /// 提供确定性的编辑器 Physics Grid 烘焙和新鲜度校验
    /// </summary>
    public static class NavigationGridBakeUtility
    {
        private const int MaximumRaycastHits = 128;
        private const int MaximumOverlapHits = 128;
        private const int MaximumCellCount = 4_000_000;
        private const float SampleEpsilon = 0.001f;

        /// <summary>
        /// 采样当前场景并整体更新对应的 Grid 资产
        /// </summary>
        /// <param name="authoring">待烘焙的 Grid 配置</param>
        /// <returns>创建或更新后的可检查资产</returns>
        public static NavigationGridBakeAsset Bake(NavigationGridAuthoring authoring)
        {
            // 输出完全由已保存场景、配置和稳定排序后的 Collider 集合决定
            // 物理采样和几何 Hash 读取同一次同步后的 Transform 状态
            // 所有派生字段在内存中完成后再整体替换资产
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

            // 物理采样生成基础 Cell 随后纯算法阶段派生拓扑和空间标识
            Dictionary<Collider, string> colliderKeys = BuildColliderKeyLookup(authoring);
            SampleCells(authoring, colliderKeys, cells, width, height);
            NavigationGridAlgorithms.BuildConnectivity(
                cells,
                width,
                height,
                authoring.MaximumStepHeight);
            NavigationGridAlgorithms.CalculateClearance(cells, width, height, authoring.CellSize);
            NavigationGridAlgorithms.AssignClusters(
                cells,
                width,
                height,
                authoring.ClusterSizeInCells);
            int regionCount = NavigationGridAlgorithms.AssignRegions(cells, width, height);

            // 量化发生在 Hash 和资产写入前保证两者读取同一份数据
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
        /// 校验资产来源、参数、几何和 Cell 数据是否仍与场景一致
        /// </summary>
        /// <param name="authoring">待校验的 Grid 配置</param>
        /// <param name="message">校验结果说明</param>
        /// <returns>所有新鲜度条件满足时返回 true</returns>
        public static bool TryValidateCurrentAsset(
            NavigationGridAuthoring authoring,
            out string message)
        {
            // 新鲜度按来源、版本、参数、几何和数据内容逐层校验
            // 任一摘要不一致都要求重新烘焙而不增量修补旧资产
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

            // 参数和几何分开比较可以向用户报告准确的过期原因
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
        /// 校验 Authoring 是否具备可重复烘焙的基础条件
        /// </summary>
        /// <param name="authoring">待检查的 Grid 配置</param>
        /// <param name="message">配置检查结果</param>
        /// <returns>配置和场景状态允许烘焙时返回 true</returns>
        public static bool TryValidateSettings(
            NavigationGridAuthoring authoring,
            out string message)
        {
            // 配置校验同时限制正确性和最坏内存规模
            // 未保存场景无法产生稳定 GUID 与几何身份，因而禁止烘焙
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

            // 使用 long 计算总数防止尺寸相乘先发生整数溢出
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
        /// 按固定字段顺序计算 Authoring 参数 Hash
        /// </summary>
        /// <param name="authoring">Grid 配置</param>
        /// <returns>三十二位十六进制 Hash</returns>
        public static string ComputeParameterHash(NavigationGridAuthoring authoring)
        {
            // 字段写入顺序属于资产兼容契约，调整顺序会使现有资产过期
            // Terrain Cost 规则顺序影响 Layer 匹配优先级，因而不能排序
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
        /// 按稳定 Collider 顺序计算场景几何 Hash
        /// </summary>
        /// <param name="authoring">限定场景、Layer 和 Bounds 的 Grid 配置</param>
        /// <returns>三十二位十六进制 Hash</returns>
        public static string ComputeGeometryHash(NavigationGridAuthoring authoring)
        {
            // Collider 先转换为稳定键再排序以消除 Unity 查找顺序影响
            // 同时写入外部依赖摘要捕获 Mesh 和 TerrainData 变化
            // Collider Bounds 由 Physics 世界维护 Hash 和实际采样必须读取同一份同步状态
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

            // 排序只使用跨会话稳定键，不使用 InstanceId
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
        /// 把稳定 Cell 索引转换为地表世界中心
        /// </summary>
        /// <param name="bakeAsset">包含尺寸和高度的 Grid 资产</param>
        /// <param name="index">Cell 行主序索引</param>
        /// <param name="verticalOffset">用于 Gizmo 的垂直偏移</param>
        /// <returns>目标 Cell 的世界中心</returns>
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

        // 每个 Cell 从包围盒顶部向下寻找稳定地面命中
        // 地面支撑、坡度和角色体积阻挡共同决定基础 Walkable
        // 此阶段不建立邻接和 Clearance 保持物理采样与拓扑推导分离
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

        // 使用无分配射线收集候选地面并按距离和稳定键选择唯一命中
        // 只接受当前场景对象防止其他已加载场景污染烘焙结果
        // 命中数量达到缓存上限时主动失败避免静默截断候选集合
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

        // 中心命中不足以证明完整脚底受支撑
        // 圆周支撑采样保守拒绝窄边缘、悬空位置和断裂平台
        // 支撑点必须与中心命中保持可接受的高度连续性
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

            // 环形支撑采样防止中心落在地面但 Agent 脚底跨出悬崖或窄平台
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

        // 用基础角色体积检查地面上方是否存在静态阻挡
        // 采样体积略微离开支撑面避免把地面自身当成障碍
        // Trigger 和不相关 Layer 不进入正式阻挡判断
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

        // 当前场景已有资产时原地更新以保持引用稳定
        // 复制场景沿用旧资产时创建独立副本避免覆盖来源数据
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

                // 复制 Scene 会保留原 SO 引用，新场景必须创建独立资产避免覆盖来源场景数据
                return CreateBakeAsset(authoring);
            }

            return CreateBakeAsset(authoring);
        }

        // 输出目录和前缀遵循项目资源命名规范
        // 唯一路径生成防止同名场景或历史资产被静默覆盖
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

        // AssetDatabase 只能逐级创建目录
        // 已存在层级直接复用使调用保持幂等
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

        // 场景名进入资产路径前移除文件系统非法字符
        // 空结果使用稳定回退名称避免生成不可定位资产
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

        // 采样热路径通过字典复用稳定键
        // 这样每次射线命中不需要重复访问 AssetDatabase 和 Hierarchy
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

        // Unity 对象查找顺序不参与任何确定性结果
        // 此处只筛选场景 Layer、Bounds 和启用状态，后续再按稳定键排序
        // Trigger 与无效 Collider 不参与几何 Hash 或物理采样
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

        // 持久化资产使用项目路径，场景组件使用场景与 Hierarchy 路径
        // 稳定身份不能依赖跨会话变化的 InstanceId
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

        // Hierarchy 路径加入同名兄弟索引以区分名称相同的对象
        // 未保存场景无法形成跨会话稳定身份并由上层校验拒绝
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

        // Collider 序列化字段不足以覆盖外部 Mesh 和 TerrainData 依赖
        // 依赖资产变化必须传播到几何 Hash 并触发重新烘焙
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

        // 空依赖写入显式占位确保字段位置保持稳定
        // 持久化依赖同时写入路径和 Asset Dependency Hash
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

        // 物理结果量化后再进入 Data Hash 和运行时 Blob
        // 统一精度吸收无业务意义的浮点尾差并提高跨机器可重复性
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

        // 中点远离零舍入让正负值采用对称规则
        private static float Quantize(float value)
        {
            return Mathf.Round(value * 10_000f) / 10_000f;
        }

        // 内存烘焙结果与持久化资产使用完全相同的字段顺序
        // Hash 覆盖头部元数据和每个 Cell 的全部运行时字段
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

        // 从资产重新计算内容 Hash 用于发现手工修改和序列化损坏
        // 此重载必须与内存结果重载保持字节级一致
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

        // 头部字段顺序属于 Data Hash 格式的一部分
        // 修改字段集合时必须同步提升 DataVersion 并更新两条计算路径
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

        // Cell 按行主序写入，任何重排都会产生不同 Data Hash
        // 委托让数组和 ScriptableObject 共用同一序列化顺序
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
            // Collider 与稳定键绑定后才能脱离 Unity 查找顺序进行排序
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
        // 所有值按固定字段顺序写入内存流
        // 字符串显式写入 UTF8 长度避免相邻值产生拼接歧义
        // Finish 后禁止继续复用防止逻辑摘要包含额外字段
        private readonly MemoryStream _stream = new MemoryStream();
        private readonly BinaryWriter _writer;
        private bool _finished;

        public NavigationGridHashWriter()
        {
            // 保持底层流打开以便 Flush 后从起点计算摘要
            _writer = new BinaryWriter(_stream, Encoding.UTF8, true);
        }

        public void Append(string value)
        {
            // 长度前缀区分字符串边界，空引用按空字符串处理
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
            // 向量始终按 X Y Z 顺序展开
            Append(value.x);
            Append(value.y);
            Append(value.z);
        }

        public void Append(Bounds value)
        {
            // Bounds 使用中心和尺寸保持 Unity 序列化语义一致
            Append(value.center);
            Append(value.size);
        }

        public void Append(Matrix4x4 value)
        {
            // 矩阵按固定行列顺序展开避免平台内存布局差异
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
            // SHA256 提供稳定摘要，最终截取前十六字节形成 128 位文本
            // 截断结果只用于变化检测，不作为安全签名
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
            // Writer 和底层流都由当前实例独占
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
#endif
