using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// 配置立方体测试角色的移动速度
/// </summary>
public class CubeAuthoring : MonoBehaviour
{
    [Range(0.1f, 20f)]
    public float moveSpeed = 4f;
}

/// <summary>
/// 保存角色移动速度
/// </summary>
public struct MoveSpeed : IComponentData { public float Value; }

/// <summary>
/// 负责烘焙立方体测试角色组件
/// </summary>
public class CubeAuthoringBaker : Baker<CubeAuthoring>
{
    /// <summary>
    /// 创建可接收网络输入命令的立方体实体
    /// </summary>
    /// <param name="authoring">立方体 Authoring 配置</param>
    public override void Bake(CubeAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);

        AddComponent<PlayerTag>(entity);
        AddComponent(entity, new MoveSpeed { Value = authoring.moveSpeed });

        AddBuffer<InputCommand>(entity); // 输入命令按网络 Tick 缓冲以支持预测回滚
    }
}
