namespace AnimarsCatcher.Player
{
    using System;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;

    /// <summary>
    /// 保存玩家控制 Entity 当前绑定的角色和相机
    /// </summary>
    [GhostComponent]
    public struct ThirdPersonPlayerControl : IComponentData
    {
        [GhostField]
        public Entity ControlledCharacter;

        [GhostField]
        public Entity ControlledCamera;
    }
}
