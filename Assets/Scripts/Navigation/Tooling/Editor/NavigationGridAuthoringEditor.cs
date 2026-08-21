#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    [CustomEditor(typeof(NavigationGridAuthoring))]
    internal sealed class NavigationGridAuthoringEditor : UnityEditor.Editor
    {
        // 状态区只显示最近一次手动操作的结果，不会在每次 Inspector 重绘时自动校验
        private MessageType _statusType = MessageType.None;
        private string _statusMessage = string.Empty;

        public override void OnInspectorGUI()
        {
            // 使用默认 Inspector 绘制配置，以保留 Undo 和多对象编辑支持
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var authoring = (NavigationGridAuthoring)target;
            // 图例放在预览配置旁，用户不用切到 Scene 视图也能理解颜色含义
            NavigationGridVisualizationRenderer.DrawLegend(authoring.GizmoMode);

            EditorGUILayout.Space();
            // 进入或退出 Play Mode 时禁止烘焙资产，但仍允许只读检查
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

            // 操作结果直接显示在按钮下方，避免频繁弹窗打断参数调整
            if (!string.IsNullOrWhiteSpace(_statusMessage))
            {
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        // 组件启用且烘焙资产有效时才绘制格子预览
        // 无论哪种预览模式都绘制范围线框，用来区分烘焙范围和格子覆盖层
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

            // 邻接线与格子覆盖层采用相同抽样密度，使线和格子保持对应
            int sampleStride = NavigationGridVisualizationRenderer.GetSampleStride(
                bakeAsset.Width,
                bakeAsset.Height,
                authoring.MaximumGizmoCells);
            int sampleStart = sampleStride / 2;
            // 从每个抽样块中心绘制，减少低密度预览偏向地图边缘的问题
            for (int z = sampleStart; z < bakeAsset.Height; z += sampleStride)
            {
                for (int x = sampleStart; x < bakeAsset.Width; x += sampleStride)
                {
                    DrawNeighborLinks(bakeAsset, x + z * bakeAsset.Width);
                }
            }
        }

        // 烘焙前先保存场景，确保场景和物体拥有可重复识别的路径
        // 成功后刷新 Inspector 并选中新资产，方便立即检查结果
        private void Bake(NavigationGridAuthoring authoring)
        {
            try
            {
                // 烘焙工具负责写入并绑定资产；Editor 这里只更新界面状态
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

        // 校验操作不会修改资产，并会明确说明资产是否过期以及原因
        private void Validate(NavigationGridAuthoring authoring)
        {
            try
            {
                // 校验结果会区分“数据有效”和“场景或配置变化，需要重新烘焙”
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

        // 只有资产结构完整且格子数与宽高一致时才交给预览绘制器
        private static bool HasDrawableData(NavigationGridBakeAsset bakeAsset)
        {
            return bakeAsset != null &&
                   bakeAsset.Width > 0 &&
                   bakeAsset.Height > 0 &&
                   bakeAsset.CellCount == bakeAsset.Width * bakeAsset.Height;
        }

        // 范围线框为所有预览模式提供统一的空间参照
        private static void DrawBounds(Bounds bounds, Color color)
        {
            Color previousColor = Gizmos.color;
            Gizmos.color = color;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
            Gizmos.color = previousColor;
        }

        // 每个被抽到的格子仍显示完整邻接关系
        // 双向连接只绘制一次，避免两条线完全重合
        // 邻接线使用与格子覆盖层相同的步长，控制大地图的 Scene 绘制开销
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

                NavigationGridDirections.GetDirection(
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
