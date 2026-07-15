#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Animars.Movement.Grid.Editor
{
    [CustomEditor(typeof(NavigationGridAuthoring))]
    internal sealed class NavigationGridAuthoringEditor : UnityEditor.Editor
    {
        private MessageType _statusType = MessageType.None;
        private string _statusMessage = string.Empty;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var authoring = (NavigationGridAuthoring)target;
            NavigationGridVisualizationRenderer.DrawLegend(authoring.GizmoMode);

            EditorGUILayout.Space();
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

            if (!string.IsNullOrWhiteSpace(_statusMessage))
            {
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
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

            int sampleStride = NavigationGridVisualizationRenderer.GetSampleStride(
                bakeAsset.Width,
                bakeAsset.Height,
                authoring.MaximumGizmoCells);
            int sampleStart = sampleStride / 2;
            for (int z = sampleStart; z < bakeAsset.Height; z += sampleStride)
            {
                for (int x = sampleStart; x < bakeAsset.Width; x += sampleStride)
                {
                    DrawNeighborLinks(bakeAsset, x + z * bakeAsset.Width);
                }
            }
        }

        private void Bake(NavigationGridAuthoring authoring)
        {
            try
            {
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

        private void Validate(NavigationGridAuthoring authoring)
        {
            try
            {
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

        private static bool HasDrawableData(NavigationGridBakeAsset bakeAsset)
        {
            return bakeAsset != null &&
                   bakeAsset.Width > 0 &&
                   bakeAsset.Height > 0 &&
                   bakeAsset.CellCount == bakeAsset.Width * bakeAsset.Height;
        }

        private static void DrawBounds(Bounds bounds, Color color)
        {
            Color previousColor = Gizmos.color;
            Gizmos.color = color;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
            Gizmos.color = previousColor;
        }

        // 抽样时保留每个显示 Cell 的完整邻接信息
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
