using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在客户端场景中注册点击输入与射线结果的单例组件
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Gameplay", "AnimarsCatcher.Gameplay", "MovementRaycastContextRegistry")]
    [DisallowMultipleComponent]
    public class WorldCommandInputAuthoring : MonoBehaviour
    {
        private sealed class Baker : Baker<WorldCommandInputAuthoring>
        {
            public override void Bake(WorldCommandInputAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.None);

                AddComponent<WorldCommandClickRequest>(entity);
                AddComponent<WorldCommandRaycastResult>(entity);
                AddComponent<WorldCommandSentVersion>(entity);
            }
        }
    }
}
