using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

public class CubeAuthoring : MonoBehaviour
{
    [Range(0.1f, 20f)]
    public float moveSpeed = 4f;
}

public struct MoveSpeed : IComponentData { public float Value; }

public class CubeAuthoringBaker : Baker<CubeAuthoring>
{
    public override void Bake(CubeAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);

        AddComponent<PlayerTag>(entity);
        AddComponent(entity, new MoveSpeed { Value = authoring.moveSpeed });

        AddBuffer<ThirdPersonMoveCommand>(entity); // 给玩家实体加命令缓冲
    }
}
