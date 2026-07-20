using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在场景中声明唯一的对局结果实体
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Gameplay", "AnimarsCatcher.Gameplay", "GameResultRegistry")]
    public class GameResultAuthoring : MonoBehaviour
    {
        private sealed class Baker : Baker<GameResultAuthoring>
        {
            public override void Bake(GameResultAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent<GameResult>(entity);
            }
        }
    }
}
