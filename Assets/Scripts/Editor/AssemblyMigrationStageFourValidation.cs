#if UNITY_EDITOR
namespace AnimarsCatcher.Editor
{
    using System;
    using System.Linq;
    using AnimarsCatcher.Gameplay.Editor;
    using AnimarsCatcher.Networking;
    using AnimarsCatcher.Player;
    using UnityEditor.Compilation;
    using UnityEngine;

    /// <summary>
    /// 验证阶段四程序集归属和 Unity 序列化引用完整性
    /// </summary>
    public static class AssemblyMigrationStageFourValidation
    {
        private const string PlayerAssemblyName = "AnimarsCatcher.Player";
        private const string PlayerEditorAssemblyName = "AnimarsCatcher.Player.Editor";
        private const string NetworkingAssemblyName = "AnimarsCatcher.Networking";
        private const string NetworkingEditorAssemblyName = "AnimarsCatcher.Networking.Editor";

        /// <summary>
        /// 验证阶段四类型归属以及项目场景和 Prefab 引用
        /// </summary>
        public static void RunFromCommandLine()
        {
            ValidateAssemblyOwnership();
            GameplayAssemblyMigrationValidation.RunFromCommandLine();
            Debug.Log("程序集迁移阶段四验收通过");
        }

        private static void ValidateAssemblyOwnership()
        {
            AssertAssembly(typeof(PlayerInput), PlayerAssemblyName);
            AssertAssembly(typeof(ThirdPersonCharacterPredictedMoveSystem), PlayerAssemblyName);
            AssertAssembly(typeof(CharacterBoxInfo), PlayerAssemblyName);
            AssertAssembly(typeof(ClientCinematicState), PlayerAssemblyName);

            AssertAssembly(typeof(CustomBootstrap), NetworkingAssemblyName);
            AssertAssembly(typeof(LobbyClientJoinedNotification), NetworkingAssemblyName);
            AssertAssembly(typeof(StartClientConnectSystem), NetworkingAssemblyName);
            AssertAssembly(typeof(CharacterSpawnUtility), NetworkingAssemblyName);

            AssertEditorAssemblyExists(PlayerEditorAssemblyName);
            AssertEditorAssemblyExists(NetworkingEditorAssemblyName);
        }

        private static void AssertEditorAssemblyExists(string assemblyName)
        {
            bool exists = CompilationPipeline
                .GetAssemblies(AssembliesType.Editor)
                .Any(assembly => assembly.name == assemblyName);
            Assert(exists, $"未找到编辑器程序集 {assemblyName}");
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
