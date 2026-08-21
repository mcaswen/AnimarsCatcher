using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.EntityView
{
    /// <summary>
    /// 保存 Entity 创建托管表现对象所需的 Prefab 和类别配置
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.PlayerView", "AnimarsCatcher.Presentation", "AvatarViewPrefabReference")]
    public class EntityViewConfig : IComponentData
    {
        public GameObject ViewPrefab;
        public EntityViewKind Kind;
    }

    /// <summary>
    /// 定义 Entity 表现对象需要附加的行为类别
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.PlayerView", "AnimarsCatcher.Presentation", "AvatarViewType")]
    public enum EntityViewKind
    {
        None = 0,
        PlayerCharacter = 1,
        BlasterAni = 2,
        PickerAni = 3,
        Resource = 4,
    }

    /// <summary>
    /// 标记 Entity 已经创建对应的托管表现对象
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.PlayerView", "AnimarsCatcher.Presentation", "AvatarViewSpawnedTag")]
    public struct EntityViewSpawnedTag : IComponentData { }
}
