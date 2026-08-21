namespace AnimarsCatcher.Presentation.EntityView
{
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.Scripting.APIUpdating;

    /// <summary>
    /// 配置 Entity 对应的托管表现 Prefab 和表现类别
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.PlayerView", "AnimarsCatcher.Presentation", "AvatarViewAuthoring")]
    [DisallowMultipleComponent]
    public class EntityViewAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("ViewPrefab")]
        [SerializeField] private GameObject _viewPrefab;
        [FormerlySerializedAs("avatarViewType")]
        [FormerlySerializedAs("_avatarViewType")]
        [SerializeField] private EntityViewKind _viewKind;

        private sealed class Baker : Unity.Entities.Baker<EntityViewAuthoring>
        {
            public override void Bake(EntityViewAuthoring authoring)
            {
                Unity.Entities.Entity entity = GetEntity(Unity.Entities.TransformUsageFlags.Dynamic);
                // GameObject 引用必须存入托管组件，不能写入非托管 IComponentData
                AddComponentObject(entity, new EntityViewConfig
                {
                    ViewPrefab = authoring._viewPrefab,
                    Kind = authoring._viewKind
                });
            }
        }
    }

}
