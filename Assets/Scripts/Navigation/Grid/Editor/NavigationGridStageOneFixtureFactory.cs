#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 创建可重复生成的阶段一 Grid 烘焙验收场景
    /// </summary>
    public static class NavigationGridStageOneFixtureFactory
    {
        /// <summary>
        /// 阶段一固定验收场景路径
        /// </summary>
        public const string ScenePath = "Assets/Scenes/Benchmarks/SCN_GridBakeStage1.unity";

        /// <summary>
        /// 阶段一固定验收资产路径
        /// </summary>
        public const string BakeAssetPath =
            "Assets/SO/Navigation/SO_NavigationGrid_SCN_GridBakeStage1.asset";

        [MenuItem("Tools/Animars Catcher/Navigation/Create Stage One Fixture")]
        // 菜单入口创建固定夹具并将结果场景设为当前编辑对象
        private static void CreateFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            CreateOrUpdateFixture();
        }

        /// <summary>
        /// 供批处理命令创建或刷新固定验收场景
        /// </summary>
        public static void CreateFromCommandLine()
        {
            CreateOrUpdateFixture();
        }

        /// <summary>
        /// 重建固定几何并生成与其匹配的 Grid 资产
        /// </summary>
        /// <returns>场景中的 NavigationGridAuthoring</returns>
        public static NavigationGridAuthoring CreateOrUpdateFixture()
        {
            // 每次都从空场景重建以避免人工编辑逐渐改变固定输入
            // 已存在 Bake Asset 会原地更新并保持提交中的 GUID
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

            // 环境和 Navigation 分根节点便于人工检查 Hierarchy 与采样来源
            var environmentRoot = new GameObject("Environment");
            CreateGroundGeometry(environmentRoot.transform, groundLayer);
            CreateObstacleGeometry(environmentRoot.transform, obstacleLayer);
            CreateLighting(environmentRoot.transform);

            var navigationRoot = new GameObject("Navigation");
            NavigationGridAuthoring authoring =
                navigationRoot.AddComponent<NavigationGridAuthoring>();
            ConfigureAuthoring(authoring, groundLayer, obstacleLayer);

            // 预先绑定历史资产让 Bake Utility 选择原地更新路径
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

            // 场景先保存再同步 Physics 保证几何 Hash 和采样读取稳定身份
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

        // 地面夹具覆盖平地、斜坡、台阶和窄平台等基础采样情况
        // 所有几何使用固定尺寸和 Transform 保证 Hash 可重复
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

        // 障碍夹具覆盖独立阻挡、角点和通道 Clearance
        // Layer 与地面分离以验证配置筛选和体积查询
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

        // 固定光照只服务人工查看，不参与 Navigation 采样和 Hash
        private static void CreateLighting(Transform parent)
        {
            var lightObject = new GameObject("Directional Light");
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
        }

        // 统一创建带 BoxCollider 的场景几何并立即写入稳定名称
        // Transform 参数在创建时一次设置避免中间状态触发无关脏标记
        private static GameObject CreateBox(
            string name,
            Vector3 position,
            Vector3 scale,
            Quaternion rotation,
            int layer,
            Transform parent)
        {
            // Cube Primitive 自带 BoxCollider 可直接参与正式采样链路
            // 删除 MeshRenderer 会改变人工可见性，因而保留默认可视网格
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

        // Authoring 参数与阶段一验收断言保持同一份固定契约
        // SerializedObject 写入保证私有序列化字段沿用正式 Inspector 路径
        private static void ConfigureAuthoring(
            NavigationGridAuthoring authoring,
            int groundLayer,
            int obstacleLayer)
        {
            // 通过 SerializedProperty 写入可覆盖字段改名和 Inspector 序列化路径问题
            // 每项值都对应阶段一文档中的固定验收参数
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

        // 测试场景目录逐级创建并允许重复执行
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
