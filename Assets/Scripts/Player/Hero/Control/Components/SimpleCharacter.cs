namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;
    using UnityEngine;

    /// <summary>
    /// 保存简化角色的静态移动和碰撞配置
    /// </summary>
    [GhostComponent]
    public struct SimpleCharacter : IComponentData
    {
        public float MoveSpeed;

        public float RotationSharpness;

        public float ColliderHeight;

        public float ColliderRadius;
    }

    /// <summary>
    /// 保存简化角色本帧需要执行的移动指令
    /// </summary>
    [GhostComponent]
    public struct SimpleCharacterControl : IComponentData
    {

        public float3 MoveVector;
    }
}
