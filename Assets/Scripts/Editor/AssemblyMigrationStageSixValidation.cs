#if UNITY_EDITOR
using System;
using AnimarsCatcher.Benchmarks.LegacyNavigation;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 验证阶段六 Legacy Benchmark 程序集归属和 Prefab 引用完整性
    /// </summary>
    public static class AssemblyMigrationStageSixValidation
    {
        private const string BenchmarkAssemblyName =
            "AnimarsCatcher.Benchmarks.LegacyNavigation";
        private const string PickerPrefabPath =
            "Assets/Prefabs/Network/Anis/PFB_Ani_Picker_Entity.prefab";
        private const string BlasterPrefabPath =
            "Assets/Prefabs/Network/Anis/PFB_Ani_Blaster_Entity.prefab";
        private const string CrystalPrefabPath =
            "Assets/Prefabs/Network/Resource/PickableCrystals/PFB_PickableCrystal1_Entity.prefab";

        /// <summary>
        /// 验证 Benchmark 类型归属以及项目场景和 Prefab 引用
        /// </summary>
        public static void RunFromCommandLine()
        {
            ValidateAssemblyOwnership();
            ValidateLegacyPrefabBindings();
            AssemblyMigrationStageFiveValidation.RunFromCommandLine();
            Debug.Log("程序集迁移阶段六验收通过");
        }

        private static void ValidateAssemblyOwnership()
        {
            AssertAssembly(typeof(AniMovementFsmAuthoring));
            AssertAssembly(typeof(NavAgentAuthoring));
            AssertAssembly(typeof(AniPhysicsAuthoring));
            AssertAssembly(typeof(ServerNavMeshPlannerSystem));
            AssertAssembly(typeof(ServerResourceCarrySetupSystem));
            AssertAssembly(typeof(AniMoveIntent));
        }

        private static void ValidateLegacyPrefabBindings()
        {
            ValidateAniPrefab(PickerPrefabPath);
            ValidateAniPrefab(BlasterPrefabPath);

            GameObject crystalPrefab = LoadPrefab(CrystalPrefabPath);
            Assert(
                crystalPrefab.GetComponentInChildren<NavAgentAuthoring>(true) != null,
                $"Legacy 资源 Prefab 缺少 NavAgentAuthoring: {CrystalPrefabPath}");
        }

        private static void ValidateAniPrefab(string prefabPath)
        {
            GameObject prefab = LoadPrefab(prefabPath);
            Assert(
                prefab.GetComponentInChildren<AniMovementFsmAuthoring>(true) != null,
                $"Legacy Ani Prefab 缺少 AniMovementFsmAuthoring: {prefabPath}");
            Assert(
                prefab.GetComponentInChildren<NavAgentAuthoring>(true) != null,
                $"Legacy Ani Prefab 缺少 NavAgentAuthoring: {prefabPath}");
            Assert(
                prefab.GetComponentInChildren<AniPhysicsAuthoring>(true) != null,
                $"Legacy Ani Prefab 缺少 AniPhysicsAuthoring: {prefabPath}");
        }

        private static GameObject LoadPrefab(string prefabPath)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert(prefab != null, $"无法加载 Prefab: {prefabPath}");
            return prefab;
        }

        private static void AssertAssembly(Type type)
        {
            string actualAssemblyName = type.Assembly.GetName().Name;
            Assert(
                actualAssemblyName == BenchmarkAssemblyName,
                $"类型 {type.FullName} 位于 {actualAssemblyName} 而不是 {BenchmarkAssemblyName}");
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
