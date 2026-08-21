#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 创建包含平地、坡度、台阶、窄平台和障碍的固定导航网格烘焙测试场景
    /// </summary>
    public static class NavigationGridStageOneFixtureFactory
    {
        // 阶段一固定测试场景路径
        public const string ScenePath = "Assets/Scenes/Benchmarks/SCN_GridBakeStage1.unity";

        // 阶段一固定烘焙资产路径
        public const string BakeAssetPath =
            "Assets/SO/Navigation/SO_NavigationGrid_SCN_GridBakeStage1.asset";

        [MenuItem("Tools/Animars Catcher/Navigation/Create Stage One Fixture")]
        // 菜单命令重建测试场景，并将它设为当前编辑场景
        private static void CreateFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            CreateOrUpdateFixture();
        }

        /// <summary>
        /// 供批处理命令创建或刷新固定测试场景
        /// </summary>
        public static void CreateFromCommandLine()
        {
            CreateOrUpdateFixture();
        }

        /// <summary>
        /// 从空场景重建固定几何，并生成与之匹配的导航网格资产
        /// </summary>
        /// <returns>场景中的 NavigationGridAuthoring</returns>
        public static NavigationGridAuthoring CreateOrUpdateFixture()
        {
            // 每次从空场景重建，避免人工修改逐渐污染固定输入
            // 已有烘焙资产会原地更新，保持版本库中的 GUID 不变
            EnsureFolder("Assets/Scenes/Benchmarks");
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);

            int groundLayer = LayerMask.NameToLayer("Ground");
            int obstacleLayer = LayerMask.NameToLayer("Default");
            if (groundLayer < 0 || obstacleLayer < 0)
            {
                throw new InvalidOperationException("阶段一夹具需要 Ground 和 Default Layer");
            }

            // 环境几何和导航配置使用不同根节点，方便在 Hierarchy 中人工检查
            var environmentRoot = new GameObject("Environment");
            CreateGroundGeometry(environmentRoot.transform, groundLayer);
            CreateObstacleGeometry(environmentRoot.transform, obstacleLayer);
            CreateLighting(environmentRoot.transform);

            var navigationRoot = new GameObject("Navigation");
            NavigationGridAuthoring authoring =
                navigationRoot.AddComponent<NavigationGridAuthoring>();
            ConfigureAuthoring(authoring, groundLayer, obstacleLayer);

            // 如果已有历史资产，先绑定它，让烘焙工具原地更新
            NavigationGridBakeAsset existingAsset =
                AssetDatabase.LoadAssetAtPath<NavigationGridBakeAsset>(BakeAssetPath);
            if (existingAsset != null)
            {
                authoring.AssignBakeAsset(existingAsset);
            }

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                throw new InvalidOperationException("无法保存阶段一 Grid 验收场景");
            }

            // 先保存场景再同步 Physics，使几何哈希和物理采样都能读取固定对象路径
            Physics.SyncTransforms();
            NavigationGridBakeAsset bakeAsset = NavigationGridBakeUtility.Bake(authoring);
            if (AssetDatabase.GetAssetPath(bakeAsset) != BakeAssetPath && existingAsset == null)
            {
                throw new InvalidOperationException(
                    $"阶段一 Grid 资产路径不符合预期: {AssetDatabase.GetAssetPath(bakeAsset)}");
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = authoring.gameObject;
            Debug.Log($"阶段一 Grid 验收场景已生成: {ScenePath}");
            return authoring;
        }

        // 地面包含平地、斜坡、台阶和窄平台，覆盖基础可行走采样情况
        // 所有物体使用固定名称、尺寸和 Transform，让重复生成得到相同哈希
        private static void CreateGroundGeometry(Transform parent, int layer)
        {
            CreateBox(
                "Ground_Main",
                new Vector3(-4f, -0.25f, 0f),
                new Vector3(14f, 0.5f, 16f),
                Quaternion.identity,
                layer,
                parent);

            CreateBox(
                "Ground_Island",
                new Vector3(8f, -0.25f, 0f),
                new Vector3(4f, 0.5f, 4f),
                Quaternion.identity,
                layer,
                parent);

            CreateBox(
                "Ground_WalkableSlope",
                new Vector3(-7.5f, 0.65f, 5f),
                new Vector3(4f, 0.5f, 3f),
                Quaternion.Euler(0f, 0f, 20f),
                layer,
                parent);

            CreateBox(
                "Ground_SteepSlope",
                new Vector3(-1.5f, 1.1f, 5f),
                new Vector3(4f, 0.5f, 3f),
                Quaternion.Euler(0f, 0f, 50f),
                layer,
                parent);

            CreateBox(
                "Ground_HighStep",
                new Vector3(-1f, 0.75f, -5f),
                new Vector3(3f, 1.5f, 3f),
                Quaternion.identity,
                layer,
                parent);
        }

        // 障碍布局覆盖独立障碍、墙角和狭窄通道
        // 障碍与地面使用不同 Layer，用于检查筛选规则和角色体积检测
        private static void CreateObstacleGeometry(Transform parent, int layer)
        {
            CreateBox(
                "Obstacle_Corridor_West",
                new Vector3(-8f, 1f, -1.5f),
                new Vector3(0.4f, 2f, 6f),
                Quaternion.identity,
                layer,
                parent);

            CreateBox(
                "Obstacle_Corridor_East",
                new Vector3(-6.4f, 1f, -1.5f),
                new Vector3(0.4f, 2f, 6f),
                Quaternion.identity,
                layer,
                parent);

            CreateBox(
                "Obstacle_Block_North",
                new Vector3(-3f, 1f, 1.5f),
                new Vector3(1f, 2f, 1f),
                Quaternion.identity,
                layer,
                parent);

            CreateBox(
                "Obstacle_Block_East",
                new Vector3(-2f, 1f, 0.5f),
                new Vector3(1f, 2f, 1f),
                Quaternion.identity,
                layer,
                parent);
        }

        // 固定光照只用于人工查看，不参与导航采样和几何哈希
        private static void CreateLighting(Transform parent)
        {
            var lightObject = new GameObject("Directional Light");
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
        }

        // 统一创建带 BoxCollider 的几何，并立即设置固定名称和 Transform
        private static GameObject CreateBox(
            string name,
            Vector3 position,
            Vector3 scale,
            Quaternion rotation,
            int layer,
            Transform parent)
        {
            // Cube Primitive 自带 BoxCollider，可以直接进入正式物理采样
            // 保留 MeshRenderer，方便开发者在场景中查看测试几何
            GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gameObject.name = name;
            gameObject.layer = layer;
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.SetPositionAndRotation(position, rotation);
            gameObject.transform.localScale = scale;
            GameObjectUtility.SetStaticEditorFlags(
                gameObject,
                StaticEditorFlags.BatchingStatic |
                StaticEditorFlags.OccluderStatic |
                StaticEditorFlags.OccludeeStatic);
            return gameObject;
        }

        // Authoring 参数与阶段一断言使用同一组固定值
        // 通过 SerializedObject 写入私有字段，走与 Inspector 相同的序列化路径
        private static void ConfigureAuthoring(
            NavigationGridAuthoring authoring,
            int groundLayer,
            int obstacleLayer)
        {
            // 使用 SerializedProperty 可以同时检查字段名称和 Inspector 序列化配置是否仍然有效
            var serializedObject = new SerializedObject(authoring);
            serializedObject.FindProperty("_worldBounds").boundsValue = new Bounds(
                new Vector3(0f, 3f, 0f),
                new Vector3(24f, 8f, 18f));
            serializedObject.FindProperty("_cellSize").floatValue = 0.5f;
            serializedObject.FindProperty("_groundLayers").intValue = 1 << groundLayer;
            serializedObject.FindProperty("_obstacleLayers").intValue = 1 << obstacleLayer;
            serializedObject.FindProperty("_maximumSlopeDegrees").floatValue = 35f;
            serializedObject.FindProperty("_maximumStepHeight").floatValue = 0.6f;
            serializedObject.FindProperty("_baseAgentRadius").floatValue = 0.35f;
            serializedObject.FindProperty("_baseAgentHeight").floatValue = 1.5f;
            serializedObject.FindProperty("_clusterSizeInCells").intValue = 8;
            serializedObject.FindProperty("_defaultTerrainCost").floatValue = 1f;
            serializedObject.FindProperty("_gizmoMode").enumValueIndex =
                (int)NavigationGridGizmoMode.Walkability;
            serializedObject.FindProperty("_showNeighborLinks").boolValue = true;
            serializedObject.FindProperty("_maximumGizmoCells").intValue = 4096;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        // 测试场景目录按需逐级创建，重复执行不会报错
        private static void EnsureFolder(string folderPath)
        {
            string[] segments = folderPath.Split('/');
            string currentPath = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string nextPath = $"{currentPath}/{segments[i]}";
                if (!AssetDatabase.IsValidFolder(nextPath))
                {
                    AssetDatabase.CreateFolder(currentPath, segments[i]);
                }

                currentPath = nextPath;
            }
        }
    }
}
#endif
