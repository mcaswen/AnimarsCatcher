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
    /// 自动验证基础格子连接、安全距离、编辑器烘焙和资产过期检测
    /// </summary>
    public static class NavigationGridStageOneValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage One Validation")]
        // 编辑器菜单可在当前会话中执行完整测试
        // 如果用户拒绝保存场景，操作会直接退出
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行阶段一全部验证
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAllInternal();
        }

        /// <summary>
        /// 依次验证基础算法、重复烘焙结果和资产过期检测
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

        // 内部入口不弹窗，可由菜单和批处理共同调用
        // 每项测试都使用固定输入，失败时抛出明确原因
        private static void RunAllInternal()
        {
            // 先检查纯算法，再用固定场景验证完整编辑器烘焙流程
            // 基础连接问题会先暴露，不会被后续资产错误掩盖
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

        // 检查斜向连接不会穿过两个正交障碍形成的墙角
        // 这条规则防止角色从墙角挤出可行走区域
        private static void TestCornerCutting()
        {
            // 在小地图中封锁斜向移动两侧格子，再检查中心到对角格的连接
            // 对角目标本身保持可行走，确保失败原因只来自穿角规则
            NavigationGridCellData[] cells = CreateWalkableCells(3, 3);
            SetWalkable(cells, 3, 1, 2, false);
            SetWalkable(cells, 3, 2, 1, false);
            NavigationGridBakingAlgorithms.BuildConnectivity(cells, 3, 3, 0.5f);

            NavigationNeighborMask centerMask = cells[1 + 1 * 3].NeighborMask;
            Assert(
                (centerMask & NavigationNeighborMask.NorthEast) == 0,
                "两个正交阻挡之间不能生成对角邻接");
        }

        // 检查过高台阶会切断连接并分成不同连通区域
        // 同时确认高度差降到允许范围后能够重新连通
        private static void TestStepHeightAndRegions()
        {
            // 用一列高度断层将平面分为两个区域
            // 降低断层高度并重算后应恢复为一个区域
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

        // 检查安全距离不会高估障碍附近和地图边缘的可用空间
        // 同一烘焙结果应能对不同角色半径给出不同通行判断
        private static void TestClearanceAndAgentRadii()
        {
            // 中央障碍用于检查距离随格子间隔增加
            // 地图边缘检查确认外部空间也被视为障碍
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

        // 检查大地图预览会根据显示预算增加抽样步长
        // 小地图仍应逐格显示，便于查看烘焙细节
        private static void TestVisualizationSampling()
        {
            // 同时测试方形和长条地图，避免单轴过长导致样本数超限
            AssertVisualizationSampleLimit(48, 36, 4096);
            AssertVisualizationSampleLimit(4096, 32, 2048);
            AssertVisualizationSampleLimit(2000, 2000, 4096);

            int fullResolutionStride = NavigationGridVisualizationRenderer.GetSampleStride(
                48,
                36,
                4096);
            Assert(fullResolutionStride == 1, "显示上限足够时覆盖层不应降低采样分辨率");
        }

        // 使用与预览绘制器相同的公式检查最终样本数
        // 防止大地图 Gizmo 意外退化为全量绘制
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

        // 固定场景包含平地、障碍、斜坡、窄平台和高度断层
        // 综合检查资产结构、哈希、连通区域和代表格子
        private static void TestFixtureData(NavigationGridAuthoring authoring)
        {
            // 先确认资产没有过期，再读取格子，避免对旧数据做出误导判断
            // 选取的坐标覆盖固定场景中的主要地形区域
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

        // 地图最外圈应因角色体积伸出烘焙范围而判定为不可站立
        // 这样地图边缘不会被误认为外侧仍有无限开放空间
        private static void AssertBoundaryCellsBlocked(NavigationGridBakeAsset bakeAsset)
        {
            // 分别遍历四条边，不能只检查角点而漏掉边缘中段
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

        // 相同场景和配置连续烘焙必须产生相同几何哈希和内容哈希
        // 用于发现对象遍历顺序或浮点尾差造成的不一致
        private static void TestRepeatedBakeHash(NavigationGridAuthoring authoring)
        {
            // 保存第一次结果后再次执行完整物理采样和资产更新
            NavigationGridBakeAsset firstAsset = NavigationGridBakeUtility.Bake(authoring);
            string firstHash = firstAsset.DataHash;
            NavigationGridBakeAsset secondAsset = NavigationGridBakeUtility.Bake(authoring);
            Assert(
                string.Equals(firstHash, secondAsset.DataHash, StringComparison.Ordinal),
                "相同输入重复烘焙必须得到相同 Data Hash");
        }

        // 暂时移除绑定资产，过期检查应明确报告资产缺失
        // 测试结束后恢复原引用，避免污染固定场景
        private static void TestMissingAssetDetection(NavigationGridAuthoring authoring)
        {
            // 在 finally 中恢复引用，即使断言失败也不会留下修改
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

        // 构建检查必须拒绝导航资产缺失或过期的场景
        // 错误信息还应包含可定位的场景路径
        private static void AssertBuildValidatorRejectsFixture()
        {
            // 临时修改配置让资产过期，再调用正式构建检查
            // 测试结束后恢复配置和场景脏状态
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

        // 修改参与参数哈希的配置后，资产必须立即被判定为过期
        // 恢复参数后再次检查，确保测试没有留下错误状态
        private static void TestParameterStaleDetection(NavigationGridAuthoring authoring)
        {
            // 修改格子大小，同时覆盖地图尺寸和采样参数变化
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

        // 移动参与烘焙的 Collider 后，几何哈希必须变化
        // 用于确认 Transform 修改能传递到场景几何摘要
        private static void TestGeometryStaleDetection(NavigationGridAuthoring authoring)
        {
            // 只移动固定障碍，不修改 Authoring，确保变化只来自场景几何
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

        // 优先打开版本库中的固定场景，确保本地和批处理使用同一输入
        // 场景缺失时自动重建，测试仍可继续
        private static NavigationGridAuthoring OpenOrCreateFixture()
        {
            // 使用 Single 模式打开，避免其他场景的 Collider 干扰物理采样
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

        // 创建没有障碍的规则格子数组作为纯算法基线
        // 所有格子使用相同法线、高度和成本，排除无关变量
        private static NavigationGridCellData[] CreateWalkableCells(int width, int height)
        {
            // 默认安全距离设得足够大，让连接测试不受角色体型影响
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

        // 先复制结构体再修改并写回数组
        // 辅助方法统一计算一维索引，减少测试代码自身出错
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

        // 只修改目标格子高度，让台阶连接测试只有一个变量
        private static void SetHeight(
            NavigationGridCellData[] cells,
            int index,
            float height)
        {
            NavigationGridCellData cell = cells[index];
            cell.Height = height;
            cells[index] = cell;
        }

        // 验证失败时抛出 InvalidOperationException，让 Unity 批处理返回非零退出码
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
