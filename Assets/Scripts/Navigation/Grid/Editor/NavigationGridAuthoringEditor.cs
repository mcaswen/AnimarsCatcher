#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    [CustomEditor(typeof(NavigationGridAuthoring))]
    internal sealed class NavigationGridAuthoringEditor : UnityEditor.Editor
    {
        // 状态区只显示最近一次按钮操作结果，不跟随每次 Inspector 重绘重新校验
        private MessageType _statusType = MessageType.None;
        private string _statusMessage = string.Empty;

        public override void OnInspectorGUI()
        {
            // 默认 Inspector 负责序列化字段 Undo 和多对象编辑基础行为
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var authoring = (NavigationGridAuthoring)target;
            // 图例紧邻显示配置让用户无需切换 Scene 视图理解颜色语义
            NavigationGridVisualizationRenderer.DrawLegend(authoring.GizmoMode);

            EditorGUILayout.Space();
            // 修改资产的烘焙在 PlayMode 切换期间禁用，只读校验和检查仍可使用
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           authoring == null || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("烘焙 Grid"))
                    {
                        Bake(authoring);
                    }
                }

                if (GUILayout.Button("校验数据"))
                {
                    Validate(authoring);
                }

                if (GUILayout.Button("打开数据检查"))
                {
                    NavigationGridInspectorWindow.Open(authoring, authoring.BakeAsset);
                }
            }

            // 操作结果固定显示在按钮下方避免对话框打断连续调参
            if (!string.IsNullOrWhiteSpace(_statusMessage))
            {
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        // Scene Gizmo 只在组件启用且资产有效时进入 Renderer
        // Bounds 始终绘制以区分采样范围和 Cell 覆盖层
        private static void DrawNavigationGridGizmo(
            NavigationGridAuthoring authoring,
            GizmoType _)
        {
            if (authoring == null || authoring.GizmoMode == NavigationGridGizmoMode.Disabled)
            {
                return;
            }

            NavigationGridBakeAsset bakeAsset = authoring.BakeAsset;
            if (!HasDrawableData(bakeAsset))
            {
                DrawBounds(authoring.WorldBounds, new Color(1f, 0.65f, 0.1f, 0.9f));
                return;
            }

            DrawBounds(bakeAsset.WorldBounds, new Color(0.2f, 0.8f, 1f, 0.75f));
            NavigationGridVisualizationRenderer.Draw(authoring, bakeAsset);

            if (!authoring.ShowNeighborLinks)
            {
                return;
            }

            // 邻接线使用与覆盖层相同的采样预算保持两层视觉对应
            int sampleStride = NavigationGridVisualizationRenderer.GetSampleStride(
                bakeAsset.Width,
                bakeAsset.Height,
                authoring.MaximumGizmoCells);
            int sampleStart = sampleStride / 2;
            // 从采样块中心开始绘制减少大步长下的边缘偏置
            for (int z = sampleStart; z < bakeAsset.Height; z += sampleStride)
            {
                for (int x = sampleStart; x < bakeAsset.Width; x += sampleStride)
                {
                    DrawNeighborLinks(bakeAsset, x + z * bakeAsset.Width);
                }
            }
        }

        // Inspector 烘焙前保存场景以建立稳定几何身份
        // 成功后刷新序列化对象并选中新资产便于立即检查
        private void Bake(NavigationGridAuthoring authoring)
        {
            try
            {
                // Bake Utility 负责保存结果和绑定资产 Editor 只刷新交互状态
                NavigationGridBakeAsset bakeAsset = NavigationGridBakeUtility.Bake(authoring);
                _statusType = MessageType.Info;
                _statusMessage = $"烘焙完成 {bakeAsset.Width} x {bakeAsset.Height}";
                EditorGUIUtility.PingObject(bakeAsset);
                SceneView.RepaintAll();
            }
            catch (Exception exception)
            {
                _statusType = MessageType.Error;
                _statusMessage = exception.Message;
                Debug.LogException(exception, authoring);
            }
        }

        // 校验入口不修改资产并用对话框反馈明确的新鲜度原因
        private void Validate(NavigationGridAuthoring authoring)
        {
            try
            {
                // 校验结果区分有效数据和需要重新烘焙的可恢复问题
                bool valid = NavigationGridBakeUtility.TryValidateCurrentAsset(
                    authoring,
                    out string message);
                _statusType = valid ? MessageType.Info : MessageType.Warning;
                _statusMessage = message;
            }
            catch (Exception exception)
            {
                _statusType = MessageType.Error;
                _statusMessage = exception.Message;
                Debug.LogException(exception, authoring);
            }
        }

        // Renderer 只接受结构可用且 Cell 数与尺寸一致的资产
        private static bool HasDrawableData(NavigationGridBakeAsset bakeAsset)
        {
            return bakeAsset != null &&
                   bakeAsset.Width > 0 &&
                   bakeAsset.Height > 0 &&
                   bakeAsset.CellCount == bakeAsset.Width * bakeAsset.Height;
        }

        // Bounds 线框作为所有可视化模式共享的空间参照
        private static void DrawBounds(Bounds bounds, Color color)
        {
            Color previousColor = Gizmos.color;
            Gizmos.color = color;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
            Gizmos.color = previousColor;
        }

        // 抽样时保留每个显示 Cell 的完整邻接信息
        // 邻接线只从固定半数方向绘制避免双向边重复显示
        // 采样步长与 Cell 覆盖层一致保证大 Grid 的调试成本受控
        private static void DrawNeighborLinks(
            NavigationGridBakeAsset bakeAsset,
            int cellIndex)
        {
            NavigationGridCellData cell = bakeAsset.GetCell(cellIndex);
            if (!cell.Walkable)
            {
                return;
            }

            int sourceX = cellIndex % bakeAsset.Width;
            int sourceZ = cellIndex / bakeAsset.Width;
            Vector3 source = NavigationGridBakeUtility.GetCellCenter(
                bakeAsset,
                cellIndex,
                Mathf.Max(0.03f, bakeAsset.CellSize * 0.06f));

            Color previousColor = Gizmos.color;
            Gizmos.color = new Color(1f, 1f, 1f, 0.55f);

            for (int directionIndex = 0; directionIndex < 8; directionIndex++)
            {
                NavigationNeighborMask directionMask =
                    (NavigationNeighborMask)(1 << directionIndex);
                if ((cell.NeighborMask & directionMask) == 0)
                {
                    continue;
                }

                NavigationGridAlgorithms.GetDirection(
                    directionIndex,
                    out int deltaX,
                    out int deltaZ);
                int targetX = sourceX + deltaX;
                int targetZ = sourceZ + deltaZ;
                if (targetX < 0 || targetX >= bakeAsset.Width ||
                    targetZ < 0 || targetZ >= bakeAsset.Height)
                {
                    continue;
                }

                int targetIndex = targetX + targetZ * bakeAsset.Width;
                Vector3 target = NavigationGridBakeUtility.GetCellCenter(
                    bakeAsset,
                    targetIndex,
                    Mathf.Max(0.03f, bakeAsset.CellSize * 0.06f));
                Gizmos.DrawLine(source, target);
            }

            Gizmos.color = previousColor;
        }
    }
}
#endif
