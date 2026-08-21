namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using Unity.Mathematics;
    using System;
    using Unity.NetCode;

    /// <summary>
    /// 保存固定相机在客户端和服务器间同步的配置
    /// </summary>
    [Serializable]
    [GhostComponent]
    public struct FixedCamera : IComponentData
    {
        [GhostField]
        public float Distance;

        [GhostField]
        public float PitchDeg;

        [GhostField]
        public float YawDeg;

        [GhostField]
        public float Height;

        // 位置变化的阻尼时长
        [GhostField]
        public float Damping;

        [GhostField]
        public float LookUpBias;

        // 网络状态发生较大偏差时直接吸附，避免阻尼追赶造成长时间错位
        [GhostField]
        public float SnapDistance;

        [GhostField]
        public float SnapAngleDeg;

    }

    /// <summary>
    /// 保存固定相机当前跟随的角色 Entity
    /// </summary>
    [Serializable]
    public struct FixedCameraControl : IComponentData
    {
        [GhostField]
        public Entity FollowedCharacterEntity;
    }

    /// <summary>
    /// 保存固定相机阻尼计算所需的跨帧速度
    /// </summary>
    [Serializable]
    public struct FixedCameraSmoothState : IComponentData
    {
        [GhostField]
        public float3 Velocity;
    }
}
