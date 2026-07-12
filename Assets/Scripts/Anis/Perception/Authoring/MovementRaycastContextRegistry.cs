using Unity.Entities;
using UnityEngine;

/// <summary>
/// 在客户端场景中注册点击输入与射线结果的单例组件
/// </summary>
[DisallowMultipleComponent]
public class MovementRaycastContextRegistry : MonoBehaviour
{
    /// <summary>
    /// 创建点击请求、结果和消费版本组件
    /// </summary>
    class Baker : Baker<MovementRaycastContextRegistry>
    {
        /// <summary>
        /// 烘焙客户端点击管线使用的共享状态
        /// </summary>
        /// <param name="authoring">点击射线注册组件</param>
        public override void Bake(MovementRaycastContextRegistry authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);

            AddComponent<MovementClickRequest>(entity);
            AddComponent<MovementClickResult>(entity);
            AddComponent<MovementClickProcessedVersion>(entity);
        }
    }
}
