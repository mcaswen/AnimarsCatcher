using Unity.Entities;

namespace AnimarsCatcher.Networking
{
    /// <summary>
    /// 保存服务器角色 Ghost Prefab 的单例引用
    /// </summary>
    public struct CharacterGhostPrefabReference : IComponentData
    {
        public Entity Value;
    }

    /// <summary>
    /// 保存玩家相机 Ghost Prefab 的单例引用
    /// </summary>
    public struct CameraGhostPrefabReference : IComponentData
    {
        public Entity Value;
    }
}
