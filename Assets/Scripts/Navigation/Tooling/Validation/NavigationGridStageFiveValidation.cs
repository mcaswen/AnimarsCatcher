#if UNITY_EDITOR
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 验证动态 Overlay 所有权和自适应阵型规则的确定性
    /// </summary>
    public static class NavigationGridStageFiveValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Five Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity batchmode 调用阶段五全部校验
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 依次执行纯算法和 Overlay 生命周期校验
        /// </summary>
        public static void RunAll()
        {
            TestAdaptiveColumnsAndRoles();
            TestMinimumCostAssignment();
            TestOverlayReferenceCounting();
            TestOverlayDeltaValidation();
            Debug.Log("Navigation Grid Stage Five validation passed");
        }

        private static void TestMinimumCostAssignment()
        {
            using NativeArray<float> costs = new(
                new[]
                {
                    9f, 1f, 8f,
                    1f, 9f, 8f,
                    8f, 8f, 1f,
                },
                Allocator.Temp);
            using NativeArray<int> first = new(3, Allocator.Temp);
            using NativeArray<int> second = new(3, Allocator.Temp);

            Assert(
                AniSquadFormationAlgorithms.TrySolveMinimumCostAssignment(
                    costs,
                    3,
                    3,
                    first),
                "Minimum-cost assignment failed");
            Assert(first[0] == 1 && first[1] == 0 && first[2] == 2,
                "Minimum-cost assignment did not select the global optimum");
            Assert(
                AniSquadFormationAlgorithms.TrySolveMinimumCostAssignment(
                    costs,
                    3,
                    3,
                    second),
                "Repeated minimum-cost assignment failed");
            for (int index = 0; index < first.Length; index++)
            {
                Assert(first[index] == second[index],
                    "Minimum-cost assignment is not deterministic");
            }
        }

        private static void TestAdaptiveColumnsAndRoles()
        {
            Assert(
                AniSquadFormationAlgorithms.CalculateAdaptiveColumnCount(
                    AniSquadFormationKind.CompactRectangle,
                    12,
                    2.0f,
                    0.7f,
                    0.4f) == 2,
                "Adaptive width should permit two columns");
            Assert(
                AniSquadFormationAlgorithms.CalculateAdaptiveColumnCount(
                    AniSquadFormationKind.CompactRectangle,
                    12,
                    0.1f,
                    0.7f,
                    0.4f) == 1,
                "Narrow width should collapse to one column");
            Assert(
                AniSquadFormationAlgorithms.CalculateAdaptiveColumnCount(
                    AniSquadFormationKind.Column,
                    12,
                    99f,
                    0.7f,
                    0.4f) == 1,
                "Column formation must remain one column");
            Assert(
                AniSquadFormationAlgorithms.CalculateSlotRole(0, 6, 2) == AniSquadRole.Picker,
                "Front row should prefer Picker");
            Assert(
                AniSquadFormationAlgorithms.CalculateSlotRole(4, 6, 2) == AniSquadRole.Blaster,
                "Back row should prefer Blaster");
        }

        private static void TestOverlayReferenceCounting()
        {
            using var world = new World("Navigation Grid Stage Five Overlay", WorldFlags.Game);
            Entity entity = world.EntityManager.CreateEntity();
            DynamicBuffer<NavigationDynamicOverlayCell> cells =
                world.EntityManager.AddBuffer<NavigationDynamicOverlayCell>(entity);
            cells.Add(default);

            Assert(
                NavigationDynamicOverlayAlgorithms.ApplyDelta(
                    cells,
                    0,
                    1,
                    2.5f,
                    0.3f,
                    7u),
                "First overlay delta should change the cell");
            Assert(cells[0].BlockCount == 1, "First block reference was not recorded");
            Assert(cells[0].ExtraCost == 2.5f, "Overlay cost was not recorded");
            Assert(
                NavigationDynamicOverlayAlgorithms.ApplyDelta(
                    cells,
                    0,
                    1,
                    1.5f,
                    0.2f,
                    8u),
                "Second overlay delta should accumulate");
            Assert(cells[0].BlockCount == 2, "Overlapping block references were collapsed");

            NavigationDynamicOverlayAlgorithms.ApplyDelta(cells, 0, -1, -1.5f, -0.2f, 9u);
            Assert(cells[0].BlockCount == 1, "Removing one blocker removed both references");
            NavigationDynamicOverlayAlgorithms.ApplyDelta(cells, 0, -1, -99f, -99f, 10u);
            Assert(!NavigationDynamicOverlayAlgorithms.IsBlocked(cells, 0), "Cell remained blocked after removal");
        }

        private static void TestOverlayDeltaValidation()
        {
            using var world = new World("Navigation Grid Stage Five Validation", WorldFlags.Game);
            Entity entity = world.EntityManager.CreateEntity();
            DynamicBuffer<NavigationDynamicOverlayCell> cells =
                world.EntityManager.AddBuffer<NavigationDynamicOverlayCell>(entity);
            cells.Add(default);

            Assert(
                !NavigationDynamicOverlayAlgorithms.ApplyDelta(
                    cells,
                    -1,
                    1,
                    0f,
                    0f,
                    1u),
                "Out-of-range overlay cell was accepted");
            Assert(
                !NavigationDynamicOverlayAlgorithms.ApplyDelta(
                    cells,
                    0,
                    1,
                    float.NaN,
                    0f,
                    1u),
                "Non-finite overlay cost was accepted");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new System.InvalidOperationException(message);
            }
        }
    }
}
#endif
