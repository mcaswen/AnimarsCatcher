using UnityEngine;
using Unity.Entities;
using Unity.Physics.Authoring;
/// <summary>
/// 提供地形碰撞烘焙所需的 Terrain 和物理材质模板
/// </summary>
public sealed class TerrainColliderAuthoring : MonoBehaviour
{
    public PhysicsMaterialTemplate physicsTemplate;
    public Terrain terrain;
}
