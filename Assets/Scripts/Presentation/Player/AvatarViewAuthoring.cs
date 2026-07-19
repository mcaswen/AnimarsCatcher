namespace AnimarsCatcher.Presentation.PlayerView
{
    using Unity.Entities;
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.Scripting.APIUpdating;

    /// <summary>
    /// 配置实体对应的托管角色表现 Prefab 和表现类型
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Player", "AnimarsCatcher.Player", "AvatarViewAuthoring")]
    [DisallowMultipleComponent]
    public class AvatarViewAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("ViewPrefab")]
        [SerializeField] private GameObject _viewPrefab;
        [FormerlySerializedAs("avatarViewType")]
        [SerializeField] private AvatarViewType _avatarViewType;

        class Baker : Baker<AvatarViewAuthoring>
        {
            public override void Bake(AvatarViewAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                // GameObject 引用必须存入托管组件，不能写入非托管 IComponentData
                AddComponentObject(entity, new AvatarViewPrefabReference
                {
                    ViewPrefab = authoring._viewPrefab,
                    ViewType = authoring._avatarViewType
                });
            }
        }
    }

    /// <summary>
    /// 保存实体创建托管表现对象所需的 Prefab 引用
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Player", "AnimarsCatcher.Player", "AvatarViewPrefabReference")]
    public class AvatarViewPrefabReference : IComponentData
    {
        public GameObject ViewPrefab;
        public AvatarViewType ViewType;
    }

    /// <summary>
    /// 定义角色表现对象需要附加的行为类型
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Player", "AnimarsCatcher.Player", "AvatarViewType")]
    public enum AvatarViewType
    {
        None = 0,
        Robot = 1,
        BlasterAni = 2,
        PickerAni = 3,
        Resource = 4,
    }


    /// <summary>
    /// 标记实体已经创建对应的托管表现对象
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Player", "AnimarsCatcher.Player", "AvatarViewSpawnedTag")]
    public struct AvatarViewSpawnedTag : IComponentData {}
}
