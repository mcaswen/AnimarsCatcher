#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 在 Player 构建前拒绝缺失或过期的 Navigation Grid 资产
    /// </summary>
    internal sealed class NavigationGridBuildValidator : IPreprocessBuildWithReport
    {
        // 以最早顺序运行让过期 Grid 在耗时构建步骤开始前失败
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            // 聚合全部启用场景的失败项后一次抛出
            // 这样开发者无需多次启动构建才能发现后续场景问题
            var failures = new List<string>();
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;

            // 关闭场景和空路径不会进入 Player 构建 因而跳过校验
            for (int i = 0; i < buildScenes.Length; i++)
            {
                EditorBuildSettingsScene buildScene = buildScenes[i];
                if (!buildScene.enabled || string.IsNullOrWhiteSpace(buildScene.path))
                {
                    continue;
                }

                ValidateScene(buildScene.path, failures);
            }

            if (failures.Count > 0)
            {
                throw new BuildFailedException(
                    "Navigation Grid 构建校验失败\n" + string.Join("\n", failures));
            }
        }

        // 以 Additive 方式打开场景并恢复调用前的 SceneSetup
        // 每个 Authoring 独立报告失败原因便于一次修复全部构建阻断项
        private static void ValidateScene(string scenePath, List<string> failures)
        {
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool openedForValidation = !scene.IsValid() || !scene.isLoaded;

            try
            {
                // 已加载场景直接复用 未加载场景临时 Additive 打开
                if (openedForValidation)
                {
                    scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                }

                // 全局查找后按 Scene 过滤以覆盖禁用 GameObject 上的 Authoring
                NavigationGridAuthoring[] authorings =
                    UnityEngine.Object.FindObjectsByType<NavigationGridAuthoring>(
                        FindObjectsInactive.Include,
                        FindObjectsSortMode.None);

                for (int i = 0; i < authorings.Length; i++)
                {
                    NavigationGridAuthoring authoring = authorings[i];
                    if (authoring.gameObject.scene != scene)
                    {
                        continue;
                    }

                    if (!NavigationGridBakeUtility.TryValidateCurrentAsset(
                            authoring,
                            out string message))
                    {
                        failures.Add($"{scenePath}/{authoring.name}: {message}");
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{scenePath}: 校验异常 {exception.Message}");
            }
            finally
            {
                // 只关闭由校验器打开的场景 不改变用户原有编辑会话
                if (openedForValidation && scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }
    }
}
#endif
