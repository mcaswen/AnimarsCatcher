using Unity.Entities;
using UnityEngine;

public class GameResultRegistry : MonoBehaviour
{
    public class Baker : Baker<GameResultRegistry>
    {
        public override void Bake(GameResultRegistry authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent<GameResult>(entity);
        }
    }
}