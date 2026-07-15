#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Animars.Movement.Grid.Editor
{
    /// <summary>
    /// 执行阶段一 Grid 算法与编辑器烘焙自动验收
    /// </summary>
    public static class NavigationGridStageOneValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage One Validation")]
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

        private static void RunAllInternal()
        {
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

        private static void TestCornerCutting()
        {
            NavigationGridCellData[] cells = CreateWalkableCells(3, 3);
            SetWalkable(cells, 3, 1, 2, false);
            SetWalkable(cells, 3, 2, 1, false);
            NavigationGridAlgorithms.BuildConnectivity(cells, 3, 3, 0.5f);

            NavigationNeighborMask centerMask = cells[1 + 1 * 3].NeighborMask;
            Assert(
                (centerMask & NavigationNeighborMask.NorthEast) == 0,
                "两个正交阻挡之间不能生成对角邻接");
        }

        private static void TestStepHeightAndRegions()
        {
            NavigationGridCellData[] cells = CreateWalkableCells(4, 1);
            SetHeight(cells, 2, 2f);
            SetHeight(cells, 3, 2f);
            NavigationGridAlgorithms.BuildConnectivity(cells, 4, 1, 0.5f);
            NavigationGridAlgorithms.CalculateClearance(cells, 4, 1, 1f);
            int regionCount = NavigationGridAlgorithms.AssignRegions(cells, 4, 1);

            Assert(
                (cells[1].NeighborMask & NavigationNeighborMask.East) == 0,
                "超过最大台阶高度时不能生成连接");
            Assert(regionCount == 2, "高度断层两侧必须形成两个 Region");
            Assert(cells[0].RegionId != cells[3].RegionId, "静态孤岛必须使用不同 RegionId");
            Assert(
                cells[1].Clearance == 0f && cells[2].Clearance == 0f,
                "不可跨越高度边必须限制两侧 Clearance");
        }

        private static void TestClearanceAndAgentRadii()
        {
            NavigationGridCellData[] cells = CreateWalkableCells(5, 5);
            NavigationGridAlgorithms.BuildConnectivity(cells, 5, 5, 0.5f);
            NavigationGridAlgorithms.CalculateClearance(cells, 5, 5, 1f);

            NavigationGridCellData edgeCell = cells[0];
            NavigationGridCellData centerCell = cells[2 + 2 * 5];
            Assert(edgeCell.Clearance < centerCell.Clearance, "Grid 中心 Clearance 应大于边缘");
            Assert(
                NavigationGridAlgorithms.CanAgentOccupy(centerCell, 0.35f, 0.35f),
                "基础 Agent 不应被重复扣减半径");
            Assert(
                !NavigationGridAlgorithms.CanAgentOccupy(edgeCell, 0.8f, 0.35f),
                "更大 Agent 应被边缘 Clearance 拒绝");
            Assert(
                NavigationGridAlgorithms.CanAgentOccupy(centerCell, 0.8f, 0.35f),
                "空间足够时更大 Agent 应可占用中心 Cell");

            NavigationGridCellData[] diagonalCells = CreateWalkableCells(3, 3);
            SetWalkable(diagonalCells, 3, 2, 2, false);
            NavigationGridAlgorithms.BuildConnectivity(diagonalCells, 3, 3, 0.5f);
            NavigationGridAlgorithms.CalculateClearance(diagonalCells, 3, 3, 1f);
            Assert(
                diagonalCells[1 + 1 * 3].Clearance <= 0.7072f,
                "对角阻挡的 Clearance 不能高估到方形角点的距离");
        }

        private static void TestVisualizationSampling()
        {
            AssertVisualizationSampleLimit(48, 36, 4096);
            AssertVisualizationSampleLimit(4096, 32, 2048);
            AssertVisualizationSampleLimit(2000, 2000, 4096);

            int fullResolutionStride = NavigationGridVisualizationRenderer.GetSampleStride(
                48,
                36,
                4096);
            Assert(fullResolutionStride == 1, "显示上限足够时覆盖层不应降低采样分辨率");
        }

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

        private static void TestFixtureData(NavigationGridAuthoring authoring)
        {
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

        private static void AssertBoundaryCellsBlocked(NavigationGridBakeAsset bakeAsset)
        {
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

        private static void TestRepeatedBakeHash(NavigationGridAuthoring authoring)
        {
            NavigationGridBakeAsset firstAsset = NavigationGridBakeUtility.Bake(authoring);
            string firstHash = firstAsset.DataHash;
            NavigationGridBakeAsset secondAsset = NavigationGridBakeUtility.Bake(authoring);
            Assert(
                string.Equals(firstHash, secondAsset.DataHash, StringComparison.Ordinal),
                "相同输入重复烘焙必须得到相同 Data Hash");
        }

        private static void TestMissingAssetDetection(NavigationGridAuthoring authoring)
        {
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

        private static void AssertBuildValidatorRejectsFixture()
        {
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

        private static void TestParameterStaleDetection(NavigationGridAuthoring authoring)
        {
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

        private static void TestGeometryStaleDetection(NavigationGridAuthoring authoring)
        {
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

        private static NavigationGridAuthoring OpenOrCreateFixture()
        {
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

        private static NavigationGridCellData[] CreateWalkableCells(int width, int height)
        {
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

        private static void SetHeight(
            NavigationGridCellData[] cells,
            int index,
            float height)
        {
            NavigationGridCellData cell = cells[index];
            cell.Height = height;
            cells[index] = cell;
        }

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
