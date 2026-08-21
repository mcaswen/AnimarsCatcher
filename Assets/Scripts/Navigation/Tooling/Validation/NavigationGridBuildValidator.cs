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
    /// 在构建 Player 前检查所有启用场景，阻止缺少导航网格或烘焙结果已过期的构建
    /// </summary>
    internal sealed class NavigationGridBuildValidator : IPreprocessBuildWithReport
    {
        // 尽早执行检查，避免耗时构建开始后才发现导航网格问题
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            // 收集所有启用场景的问题后一次报告，开发者可以一次修复完毕
            var failures = new List<string>();
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;

            // 未启用或路径为空的场景不会进入 Player，因此无需检查
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

        // 临时以 Additive 方式打开场景，检查结束后恢复原有编辑器场景布局
        // 每个 Authoring 单独报告原因，便于一次找出所有阻断构建的问题
        private static void ValidateScene(string scenePath, List<string> failures)
        {
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool openedForValidation = !scene.IsValid() || !scene.isLoaded;

            try
            {
                // 已加载场景直接使用，未加载场景临时以 Additive 方式打开
                if (openedForValidation)
                {
                    scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                }

                // 全局查找后再按场景筛选，这样禁用 GameObject 上的 Authoring 也不会漏检
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
                // 只关闭本检查临时打开的场景，不改变用户原有的编辑会话
                if (openedForValidation && scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }
    }
}
#endif
