namespace AnimarsCatcher.Player
{
    using System;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;

    /// <summary>
    /// 保存相机实际跟随目标的 Entity 引用
    /// </summary>
    [Serializable]
    public struct CameraTarget : IComponentData
    {
        [GhostField]
        public Entity TargetEntity;
    }
}
