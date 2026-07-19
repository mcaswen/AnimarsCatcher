using Unity.Entities;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在客户端场景中注册点击输入与射线结果的单例组件
    /// </summary>
    [DisallowMultipleComponent]
    public class MovementRaycastContextRegistry : MonoBehaviour
    {
        class Baker : Baker<MovementRaycastContextRegistry>
        {
            public override void Bake(MovementRaycastContextRegistry authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.None);

                AddComponent<MovementClickRequest>(entity);
                AddComponent<MovementClickResult>(entity);
                AddComponent<MovementClickProcessedVersion>(entity);
            }
        }
    }
}
