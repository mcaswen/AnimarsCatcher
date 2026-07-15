#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Animars.Movement.Grid.Editor
{
    internal sealed class NavigationGridBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var failures = new List<string>();
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;

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

        private static void ValidateScene(string scenePath, List<string> failures)
        {
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool openedForValidation = !scene.IsValid() || !scene.isLoaded;

            try
            {
                if (openedForValidation)
                {
                    scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                }

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
                if (openedForValidation && scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }
    }
}
#endif
