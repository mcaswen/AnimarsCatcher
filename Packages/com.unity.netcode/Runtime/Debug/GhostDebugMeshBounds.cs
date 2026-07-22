using System.Collections.Generic;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 保存 Mesh Bounds 以供调试绘制的自包含组件
    /// </summary>
    /// <remarks>
    /// 即使 GameObject 处于非激活状态，此组件也应保持有效
    /// 它实际显示的是与 Entity 生命周期关联的 GameObject NetCode 调试框
    /// 如果 Entity 仍在移动而 GameObject 已失活，通常仍需要观察它
    /// </remarks>
    public struct GhostDebugMeshBounds : IComponentData
    {
        static List<Renderer> s_AllRenderers = new();
        /// <summary>
        /// 此 Entity 的 Bounds，用于绘制调试框
        /// 应位于局部空间，中心位于对象原点
        /// </summary>
        public Bounds GlobalBounds;

        /// <summary>
        /// 初始化 GameObject 调试 Mesh Bounds 的便捷方法
        /// </summary>
        /// <param name="gameObject">具有调试 Mesh 的 GameObject</param>
        /// <param name="entity">GameObject 对应的 Entity</param>
        /// <param name="world">包含 Entity 的 World</param>
        /// <returns>用于调试绘制的 Mesh Bounds</returns>
        public GhostDebugMeshBounds Initialize(GameObject gameObject, Entity entity, World world)
        {
            gameObject.GetComponentsInChildren<Renderer>(includeInactive: true, results: s_AllRenderers);
            world.EntityManager.AddComponent<LocalToWorld>(entity); // 调试 Drawer 渲染小十字标记时需要此组件
            if (s_AllRenderers.Count != 0)
            {
                GlobalBounds = s_AllRenderers[0].localBounds;
                GlobalBounds.center = gameObject.transform.InverseTransformPoint(s_AllRenderers[0].bounds.center); // localBounds 的中心为零，因此需要校正
                for (int i = 1; i < s_AllRenderers.Count; i++)
                {
                    var currentBounds = s_AllRenderers[i].localBounds;
                    currentBounds.center = gameObject.transform.InverseTransformPoint(s_AllRenderers[i].bounds.center); // localBounds 的中心为零，因此需要校正
                    GlobalBounds.Encapsulate(currentBounds);
                }
            }

            return this;
        }
    }
}
