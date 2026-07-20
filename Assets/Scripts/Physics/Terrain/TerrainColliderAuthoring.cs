using UnityEngine;
using Unity.Entities;
using Unity.Physics.Authoring;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Physics.Authoring
{
    /// <summary>
    /// 提供地形碰撞烘焙所需的 Terrain 和物理材质模板
    /// </summary>
    public sealed class TerrainColliderAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("physicsTemplate")]
        [SerializeField] private PhysicsMaterialTemplate _physicsMaterialTemplate;

        [FormerlySerializedAs("terrain")]
        [SerializeField] private Terrain _terrain;

        public PhysicsMaterialTemplate PhysicsMaterialTemplate => _physicsMaterialTemplate;
        public Terrain Terrain => _terrain;
    }
}
