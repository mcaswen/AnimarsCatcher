namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;

    /// <summary>
    /// 保存需要同步到角色表现层的动画参数
    /// </summary>
    public struct AvatarAnimationParameters : IComponentData
    {
        [GhostField]
        public float Speed;
    }
}
