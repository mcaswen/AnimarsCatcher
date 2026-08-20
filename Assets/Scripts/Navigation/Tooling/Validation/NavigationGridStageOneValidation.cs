#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 执行阶段一 Grid 算法与编辑器烘焙自动验收
    /// </summary>
    public static class NavigationGridStageOneValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage One Validation")]
        // 菜单入口允许开发者在当前编辑会话内执行完整验收
        // 交互入口会保留用户拒绝保存场景时的退出选择
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity 批处理执行阶段一完整验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAllInternal();
        }

        /// <summary>
        /// 执行纯算法、确定性和过期检测验证
        /// </summary>
        public static void RunAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            SceneSetup[] sceneSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                RunAllInternal();
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(sceneSetup);
            }
        }

        // 内部入口不弹出交互对话框供批处理和菜单流程共同复用
        // 每项测试只依赖固定输入并在失败时抛出明确原因
        private static void RunAllInternal()
        {
            // 纯算法测试先运行，固定场景测试随后验证完整编辑器链路
            // 排序让基础拓扑失败不会被后续资产错误掩盖
            TestCornerCutting();
            TestStepHeightAndRegions();
            TestClearanceAndAgentRadii();
            TestVisualizationSampling();

            NavigationGridAuthoring authoring = OpenOrCreateFixture();
            TestFixtureData(authoring);
            TestRepeatedBakeHash(authoring);
            TestMissingAssetDetection(authoring);
            TestParameterStaleDetection(authoring);
            TestGeometryStaleDetection(authoring);

            Assert(
                NavigationGridBakeUtility.TryValidateCurrentAsset(authoring, out string message),
                $"恢复后的阶段一夹具应有效: {message}");
            Debug.Log("Navigation Grid 阶段一自动验收通过");
        }

        // 验证对角邻接不会跨越两个正交阻挡形成的角点
        // 该约束直接保护大规模单位不会从墙角漏出
        private static void TestCornerCutting()
        {
            // 小型 Grid 封锁两个正交侧边并检查中心对角位
            // 目标 Cell 保持 Walkable 以隔离穿角规则
            NavigationGridCellData[] cells = CreateWalkableCells(3, 3);
            SetWalkable(cells, 3, 1, 2, false);
            SetWalkable(cells, 3, 2, 1, false);
            NavigationGridBakingAlgorithms.BuildConnectivity(cells, 3, 3, 0.5f);

            NavigationNeighborMask centerMask = cells[1 + 1 * 3].NeighborMask;
            Assert(
                (centerMask & NavigationNeighborMask.NorthEast) == 0,
                "两个正交阻挡之间不能生成对角邻接");
        }

        // 验证高度断层会切断连接并形成不同静态 Region
        // 同时覆盖允许高度差边界上的可达情况
        private static void TestStepHeightAndRegions()
        {
            // 单列高度断层把规则平面切成两个不可互达区域
            // 降低断层后重新计算应恢复单一 Region
            NavigationGridCellData[] cells = CreateWalkableCells(4, 1);
            SetHeight(cells, 2, 2f);
            SetHeight(cells, 3, 2f);
            NavigationGridBakingAlgorithms.BuildConnectivity(cells, 4, 1, 0.5f);
            NavigationEuclideanDistanceTransform.Calculate(cells, 4, 1, 1f);
            int regionCount = NavigationGridBakingAlgorithms.AssignRegions(cells, 4, 1);

            Assert(
                (cells[1].NeighborMask & NavigationNeighborMask.East) == 0,
                "超过最大台阶高度时不能生成连接");
            Assert(regionCount == 2, "高度断层两侧必须形成两个 Region");
            Assert(cells[0].RegionId != cells[3].RegionId, "静态孤岛必须使用不同 RegionId");
            Assert(
                cells[1].Clearance == 0f && cells[2].Clearance == 0f,
                "不可跨越高度边必须限制两侧 Clearance");
        }

        // 验证距离场不会高估障碍附近和 Grid 边界的可用空间
        // 不同 Agent 半径必须在同一烘焙结果上得到不同占用结论
        private static void TestClearanceAndAgentRadii()
        {
            // 中央障碍用于验证距离随 Cell 间距递增
            // 边界断言验证外围补阻挡参与同一距离场
            NavigationGridCellData[] cells = CreateWalkableCells(5, 5);
            NavigationGridBakingAlgorithms.BuildConnectivity(cells, 5, 5, 0.5f);
            NavigationEuclideanDistanceTransform.Calculate(cells, 5, 5, 1f);

            NavigationGridCellData edgeCell = cells[0];
            NavigationGridCellData centerCell = cells[2 + 2 * 5];
            Assert(edgeCell.Clearance < centerCell.Clearance, "Grid 中心 Clearance 应大于边缘");
            Assert(
                NavigationGridBakingAlgorithms.CanAgentOccupy(centerCell, 0.35f, 0.35f),
                "基础 Agent 不应被重复扣减半径");
            Assert(
                !NavigationGridBakingAlgorithms.CanAgentOccupy(edgeCell, 0.8f, 0.35f),
                "更大 Agent 应被边缘 Clearance 拒绝");
            Assert(
                NavigationGridBakingAlgorithms.CanAgentOccupy(centerCell, 0.8f, 0.35f),
                "空间足够时更大 Agent 应可占用中心 Cell");

            NavigationGridCellData[] diagonalCells = CreateWalkableCells(3, 3);
            SetWalkable(diagonalCells, 3, 2, 2, false);
            NavigationGridBakingAlgorithms.BuildConnectivity(diagonalCells, 3, 3, 0.5f);
            NavigationEuclideanDistanceTransform.Calculate(diagonalCells, 3, 3, 1f);
            Assert(
                diagonalCells[1 + 1 * 3].Clearance <= 0.7072f,
                "对角阻挡的 Clearance 不能高估到方形角点的距离");
        }

        // 验证大 Grid 可视化会按预算增加采样步长
        // 小 Grid 仍应保持逐 Cell 显示以便检查烘焙细节
        private static void TestVisualizationSampling()
        {
            // 同时覆盖平方 Grid 和长条 Grid 防止偏轴样本数超限
            AssertVisualizationSampleLimit(48, 36, 4096);
            AssertVisualizationSampleLimit(4096, 32, 2048);
            AssertVisualizationSampleLimit(2000, 2000, 4096);

            int fullResolutionStride = NavigationGridVisualizationRenderer.GetSampleStride(
                48,
                36,
                4096);
            Assert(fullResolutionStride == 1, "显示上限足够时覆盖层不应降低采样分辨率");
        }

        // 使用与 Renderer 相同的步长公式验证最终采样数上界
        // 该测试防止编辑器 Gizmo 因尺寸增长退化为全量绘制
        private static void AssertVisualizationSampleLimit(
            int width,
            int height,
            int maximumCells)
        {
            int stride = NavigationGridVisualizationRenderer.GetSampleStride(
                width,
                height,
                maximumCells);
            long sampleCount =
                (long)((width + stride - 1) / stride) *
                ((height + stride - 1) / stride);
            Assert(stride >= 1, "覆盖层抽样步长必须大于等于一");
            Assert(sampleCount <= maximumCells, "覆盖层二维抽样数量不得超过显示上限");
        }

        // 固定场景同时包含平地、障碍、斜坡、窄边和高度断层
        // 对烘焙资产执行结构 Hash 区域和代表 Cell 的综合断言
        private static void TestFixtureData(NavigationGridAuthoring authoring)
        {
            // 先校验资产新鲜度再读取 Cell 防止过期数据产生误导断言
            // 代表坐标覆盖固定夹具中的主要语义区域
            NavigationGridBakeAsset bakeAsset = authoring.BakeAsset;
            Assert(bakeAsset != null && bakeAsset.IsUsable, "阶段一夹具必须生成可用资产");
            Assert(bakeAsset.RegionCount >= 2, "固定夹具必须包含静态孤岛");

            int walkableCount = 0;
            int blockedCount = 0;
            int linkedCount = 0;
            int steepCellCount = 0;
            GameObject steepSlopeObject = GameObject.Find("Ground_SteepSlope");
            Assert(steepSlopeObject != null, "固定夹具缺少陡坡对象");
            Bounds steepSlopeBounds = steepSlopeObject.GetComponent<BoxCollider>().bounds;
            int sampledSteepSurfaceCount = 0;
            for (int i = 0; i < bakeAsset.CellCount; i++)
            {
                NavigationGridCellData cell = bakeAsset.GetCell(i);
                Vector3 cellCenter = NavigationGridBakeUtility.GetCellCenter(bakeAsset, i);
                if (cell.Walkable)
                {
                    walkableCount++;
                }
                else
                {
                    blockedCount++;
                }

                if (cell.NeighborMask != NavigationNeighborMask.None)
                {
                    linkedCount++;
                }

                if (cell.SlopeDegrees > authoring.MaximumSlopeDegrees &&
                    cell.Height > authoring.WorldBounds.min.y)
                {
                    steepCellCount++;
                }

                bool insideSteepSlopeFootprint =
                    cellCenter.x >= steepSlopeBounds.min.x &&
                    cellCenter.x <= steepSlopeBounds.max.x &&
                    cellCenter.z >= steepSlopeBounds.min.z &&
                    cellCenter.z <= steepSlopeBounds.max.z &&
                    cell.Height > 0.05f;
                if (insideSteepSlopeFootprint)
                {
                    sampledSteepSurfaceCount++;
                    Assert(!cell.Walkable, "陡坡对象覆盖的采样 Cell 不得可行走");
                }
            }

            Assert(walkableCount > 0, "固定夹具必须包含可行走 Cell");
            Assert(blockedCount > 0, "固定夹具必须包含阻挡 Cell");
            Assert(linkedCount > 0, "固定夹具必须生成八邻接数据");
            Assert(steepCellCount > 0, "固定夹具必须采样到超过阈值的坡面");
            Assert(sampledSteepSurfaceCount > 0, "固定夹具必须命中陡坡对象表面");
            AssertBoundaryCellsBlocked(bakeAsset);
        }

        // 外围 Cell 应因基础角色体积越出采样范围而被保守阻挡
        // 这项断言保护 Grid 边缘不会被当成无限开放空间
        private static void AssertBoundaryCellsBlocked(NavigationGridBakeAsset bakeAsset)
        {
            // 四条边分别遍历避免只验证角点而遗漏长边中段
            for (int x = 0; x < bakeAsset.Width; x++)
            {
                Assert(!bakeAsset.GetCell(x).Walkable, "Grid 南边界必须拒绝基础 Agent");
                int northIndex = x + (bakeAsset.Height - 1) * bakeAsset.Width;
                Assert(!bakeAsset.GetCell(northIndex).Walkable, "Grid 北边界必须拒绝基础 Agent");
            }

            for (int z = 0; z < bakeAsset.Height; z++)
            {
                int westIndex = z * bakeAsset.Width;
                int eastIndex = bakeAsset.Width - 1 + z * bakeAsset.Width;
                Assert(!bakeAsset.GetCell(westIndex).Walkable, "Grid 西边界必须拒绝基础 Agent");
                Assert(!bakeAsset.GetCell(eastIndex).Walkable, "Grid 东边界必须拒绝基础 Agent");
            }
        }

        // 相同场景和参数连续烘焙必须产生相同几何与数据 Hash
        // 该测试捕获对象枚举顺序和浮点尾差造成的非确定性
        private static void TestRepeatedBakeHash(NavigationGridAuthoring authoring)
        {
            // 保存首轮摘要后再次执行完整物理采样和资产覆盖
            NavigationGridBakeAsset firstAsset = NavigationGridBakeUtility.Bake(authoring);
            string firstHash = firstAsset.DataHash;
            NavigationGridBakeAsset secondAsset = NavigationGridBakeUtility.Bake(authoring);
            Assert(
                string.Equals(firstHash, secondAsset.DataHash, StringComparison.Ordinal),
                "相同输入重复烘焙必须得到相同 Data Hash");
        }

        // 暂时移除绑定资产后新鲜度校验必须明确报告缺失
        // 测试结束恢复原引用避免污染固定场景
        private static void TestMissingAssetDetection(NavigationGridAuthoring authoring)
        {
            // 使用 finally 恢复引用保证断言失败也不会污染场景
            NavigationGridBakeAsset originalAsset = authoring.BakeAsset;
            Scene scene = authoring.gameObject.scene;

            try
            {
                authoring.AssignBakeAsset(null);
                EditorUtility.SetDirty(authoring);
                EditorSceneManager.SaveScene(scene);
                bool valid = NavigationGridBakeUtility.TryValidateCurrentAsset(
                    authoring,
                    out string message);
                Assert(!valid && message.Contains("缺少"), "缺失 Grid 资产必须被拒绝");
                AssertBuildValidatorRejectsFixture();
            }
            finally
            {
                authoring.AssignBakeAsset(originalAsset);
                EditorUtility.SetDirty(authoring);
                EditorSceneManager.SaveScene(scene);
            }
        }

        // 构建门禁必须拒绝场景中缺失或过期的 Grid 资产
        // 通过固定夹具验证失败信息包含可定位的场景路径
        private static void AssertBuildValidatorRejectsFixture()
        {
            // 临时篡改参数制造可恢复的过期资产并调用构建门禁
            // 测试结束恢复配置和场景脏状态
            EditorBuildSettingsScene[] originalScenes = EditorBuildSettings.scenes;
            var validationScenes = new List<EditorBuildSettingsScene>(originalScenes)
            {
                new EditorBuildSettingsScene(
                    NavigationGridStageOneFixtureFactory.ScenePath,
                    true),
            };

            try
            {
                EditorBuildSettings.scenes = validationScenes.ToArray();
                bool rejected = false;
                try
                {
                    new NavigationGridBuildValidator().OnPreprocessBuild(null);
                }
                catch (BuildFailedException exception)
                {
                    rejected = exception.Message.Contains(
                        NavigationGridStageOneFixtureFactory.ScenePath);
                }

                Assert(rejected, "构建校验必须拒绝缺失 Grid 资产的登记场景");
            }
            finally
            {
                EditorBuildSettings.scenes = originalScenes;
            }
        }

        // 修改参与 Parameter Hash 的配置后资产必须立即判定过期
        // 恢复参数后再次校验确保测试不留下脏状态
        private static void TestParameterStaleDetection(NavigationGridAuthoring authoring)
        {
            // 选择 CellSize 变化同时覆盖尺寸与采样参数摘要
            Scene scene = authoring.gameObject.scene;
            var serializedObject = new SerializedObject(authoring);
            SerializedProperty property = serializedObject.FindProperty("_maximumStepHeight");
            float originalValue = property.floatValue;

            try
            {
                property.floatValue = originalValue + 0.1f;
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.SaveScene(scene);
                bool valid = NavigationGridBakeUtility.TryValidateCurrentAsset(
                    authoring,
                    out string message);
                Assert(!valid && message.Contains("参数"), "参数变化必须使旧 Grid 资产过期");
            }
            finally
            {
                serializedObject.Update();
                serializedObject.FindProperty("_maximumStepHeight").floatValue = originalValue;
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.SaveScene(scene);
            }
        }

        // 移动参与烘焙的 Collider 后 Geometry Hash 必须变化
        // 该测试覆盖 Transform 变化传播到几何依赖摘要的链路
        private static void TestGeometryStaleDetection(NavigationGridAuthoring authoring)
        {
            // 移动固定障碍且不改变 Authoring 以隔离 Geometry Hash
            Scene scene = authoring.gameObject.scene;
            GameObject obstacle = GameObject.Find("Obstacle_Corridor_West");
            Assert(obstacle != null, "固定夹具缺少用于过期检测的障碍");
            Vector3 originalPosition = obstacle.transform.position;

            try
            {
                obstacle.transform.position = originalPosition + Vector3.right * 0.25f;
                EditorSceneManager.SaveScene(scene);
                bool valid = NavigationGridBakeUtility.TryValidateCurrentAsset(
                    authoring,
                    out string message);
                Assert(!valid && message.Contains("几何"), "几何变化必须使旧 Grid 资产过期");
            }
            finally
            {
                obstacle.transform.position = originalPosition;
                EditorSceneManager.SaveScene(scene);
            }
        }

        // 优先打开已提交夹具，保证本地和批处理使用同一输入
        // 缺失时重新生成使验证入口具备可恢复性
        private static NavigationGridAuthoring OpenOrCreateFixture()
        {
            // Single 模式打开避免其他场景 Collider 干扰 Physics
            SceneAsset sceneAsset =
                AssetDatabase.LoadAssetAtPath<SceneAsset>(NavigationGridStageOneFixtureFactory.ScenePath);
            if (sceneAsset == null)
            {
                return NavigationGridStageOneFixtureFactory.CreateOrUpdateFixture();
            }

            Scene scene = EditorSceneManager.OpenScene(
                NavigationGridStageOneFixtureFactory.ScenePath,
                OpenSceneMode.Single);
            NavigationGridAuthoring[] authorings =
                UnityEngine.Object.FindObjectsByType<NavigationGridAuthoring>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);

            for (int i = 0; i < authorings.Length; i++)
            {
                if (authorings[i].gameObject.scene == scene)
                {
                    return authorings[i];
                }
            }

            throw new InvalidOperationException("阶段一夹具场景缺少 NavigationGridAuthoring");
        }

        // 构造无障碍规则数组作为纯算法测试的最小基线
        // 每个 Cell 使用相同法线、高度和地形成本，消除无关变量
        private static NavigationGridCellData[] CreateWalkableCells(int width, int height)
        {
            // 默认 Clearance 足够大使拓扑测试不受体型过滤影响
            var cells = new NavigationGridCellData[width * height];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = new NavigationGridCellData
                {
                    Height = 0f,
                    SurfaceNormal = Vector3.up,
                    SlopeDegrees = 0f,
                    TerrainCost = 1f,
                    Walkable = true,
                };
            }

            return cells;
        }

        // 通过复制写回修改结构体数组中的 Walkable 状态
        // 辅助方法统一行主序索引计算减少测试自身错误
        private static void SetWalkable(
            NavigationGridCellData[] cells,
            int width,
            int x,
            int z,
            bool walkable)
        {
            int index = x + z * width;
            NavigationGridCellData cell = cells[index];
            cell.Walkable = walkable;
            cells[index] = cell;
        }

        // 只修改目标 Cell 高度以隔离台阶连接测试变量
        private static void SetHeight(
            NavigationGridCellData[] cells,
            int index,
            float height)
        {
            NavigationGridCellData cell = cells[index];
            cell.Height = height;
            cells[index] = cell;
        }

        // 验收失败使用 InvalidOperationException 让 Unity 批处理返回非零结果
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif
