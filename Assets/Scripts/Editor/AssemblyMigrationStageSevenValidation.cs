#if UNITY_EDITOR
namespace AnimarsCatcher.Editor
{
    using System;
    using System.IO;
    using System.Linq;
    using AnimarsCatcher.Navigation.Grid.Editor;
    using AnimarsCatcher.Gameplay.Editor;
    using AnimarsCatcher.Physics.Authoring;
    using UnityEditor;
    using UnityEditor.Compilation;
    using UnityEngine;

    /// <summary>
    /// 验证最终程序集边界、显式引用策略和当前序列化完整性
    /// </summary>
    public static class AssemblyMigrationStageSevenValidation
    {
        private const string PhysicsAuthoringAssemblyName =
            "AnimarsCatcher.Physics.Authoring";

        private static readonly string[] ProjectAssemblyNames =
        {
            "AnimarsCatcher.Benchmarks.LegacyNavigation",
            "AnimarsCatcher.Core",
            "AnimarsCatcher.Editor",
            "AnimarsCatcher.Gameplay",
            "AnimarsCatcher.Gameplay.Contracts",
            "AnimarsCatcher.Navigation",
            "AnimarsCatcher.Navigation.Editor",
            "AnimarsCatcher.Networking",
            "AnimarsCatcher.Networking.Editor",
            "AnimarsCatcher.Physics.Authoring",
            "AnimarsCatcher.Player",
            "AnimarsCatcher.Player.Editor",
            "AnimarsCatcher.Presentation"
        };

        /// <summary>
        /// 执行最终程序集迁移验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            ValidateAssemblyAvailability();
            ValidatePhysicsAuthoringOwnership();
            ValidateAutoReferencedPolicy();

            GameplayAssemblyMigrationValidation.RunFromCommandLine();
            NavigationGridStageOneFixtureFactory.CreateFromCommandLine();
            NavigationAssemblyMigrationValidation.RunFromCommandLine();
            NavigationGridStageOneValidation.RunFromCommandLine();
            NavigationGridStageTwoValidation.RunFromCommandLine();

            Debug.Log("程序集迁移最终阶段验收通过");
        }

        private static void ValidateAssemblyAvailability()
        {
            string[] compiledAssemblyNames = CompilationPipeline
                .GetAssemblies(AssembliesType.Editor)
                .Select(assembly => assembly.name)
                .ToArray();

            foreach (string assemblyName in ProjectAssemblyNames)
            {
                Assert(
                    compiledAssemblyNames.Contains(assemblyName),
                    $"Unity 编译结果中缺少项目程序集 {assemblyName}");
            }
        }

        private static void ValidatePhysicsAuthoringOwnership()
        {
            AssertAssembly(typeof(CapsuleColliderGeometryAuthoring), PhysicsAuthoringAssemblyName);
            AssertAssembly(typeof(TerrainColliderAuthoring), PhysicsAuthoringAssemblyName);
            AssertAssembly(typeof(TerrainColliderBaker), PhysicsAuthoringAssemblyName);
        }

        private static void ValidateAutoReferencedPolicy()
        {
            string[] assemblyDefinitionGuids = AssetDatabase.FindAssets(
                "t:AssemblyDefinitionAsset",
                new[] { "Assets/Scripts" });

            Assert(
                assemblyDefinitionGuids.Length == ProjectAssemblyNames.Length,
                $"项目 asmdef 数量为 {assemblyDefinitionGuids.Length}，预期为 {ProjectAssemblyNames.Length}");

            foreach (string guid in assemblyDefinitionGuids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                string json = File.ReadAllText(assetPath);
                var settings = JsonUtility.FromJson<AssemblyDefinitionSettings>(json);
                Assert(
                    !settings.AutoReferenced,
                    $"项目程序集仍启用 Auto Referenced: {assetPath}");
            }
        }

        private static void AssertAssembly(Type type, string expectedAssemblyName)
        {
            string actualAssemblyName = type.Assembly.GetName().Name;
            Assert(
                actualAssemblyName == expectedAssemblyName,
                $"类型 {type.FullName} 位于 {actualAssemblyName} 而不是 {expectedAssemblyName}");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        [Serializable]
        private sealed class AssemblyDefinitionSettings
        {
            [SerializeField] private bool autoReferenced;

            public bool AutoReferenced => autoReferenced;
        }
    }
}
#endif
