#if UNITY_EDITOR
using System;
using System.Linq;
using AnimarsCatcher.Gameplay.Editor;
using AnimarsCatcher.Presentation.Account;
using AnimarsCatcher.Presentation.Anis;
using AnimarsCatcher.Presentation.Audio;
using AnimarsCatcher.Presentation.Global;
using AnimarsCatcher.Presentation.HealthUI;
using AnimarsCatcher.Presentation.Lan;
using AnimarsCatcher.Presentation.PlayerView;
using AnimarsCatcher.Presentation.Selection;
using AnimarsCatcher.Presentation.UI;
using UnityEditor.Compilation;
using UnityEngine;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 验证阶段五 Presentation 程序集归属和序列化引用完整性
    /// </summary>
    public static class AssemblyMigrationStageFiveValidation
    {
        private const string PresentationAssemblyName = "AnimarsCatcher.Presentation";
        private const string DotweenModulesAssemblyName = "DOTween.Modules";

        /// <summary>
        /// 验证 Presentation 类型归属以及项目场景和 Prefab 引用
        /// </summary>
        public static void RunFromCommandLine()
        {
            ValidateAssemblyOwnership();
            GameplayAssemblyMigrationValidation.RunFromCommandLine();
            Debug.Log("程序集迁移阶段五验收通过");
        }

        private static void ValidateAssemblyOwnership()
        {
            AssertAssembly(typeof(PlayerSession), PresentationAssemblyName);
            AssertAssembly(typeof(AudioManager), PresentationAssemblyName);
            AssertAssembly(typeof(NetworkPresentationBridgeSystem), PresentationAssemblyName);
            AssertAssembly(typeof(LanDiscoveryHost), PresentationAssemblyName);
            AssertAssembly(typeof(MainMenuPanelController), PresentationAssemblyName);
            AssertAssembly(typeof(AniSelectionApplyRpc), PresentationAssemblyName);
            AssertAssembly(typeof(HealthBarView), PresentationAssemblyName);
            AssertAssembly(typeof(BlasterAniAttackView), PresentationAssemblyName);
            AssertAssembly(typeof(MovementClickRaycastSystem), PresentationAssemblyName);
            AssertAssembly(typeof(AvatarViewAuthoring), PresentationAssemblyName);

            bool hasDotweenModulesAssembly = CompilationPipeline
                .GetAssemblies(AssembliesType.Editor)
                .Any(assembly => assembly.name == DotweenModulesAssemblyName);
            Assert(
                hasDotweenModulesAssembly,
                $"未找到第三方程序集 {DotweenModulesAssemblyName}");
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
    }
}
#endif
