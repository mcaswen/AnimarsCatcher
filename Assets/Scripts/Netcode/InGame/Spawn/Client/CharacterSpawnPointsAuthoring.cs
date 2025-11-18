using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public struct CharacterSpawnPointsTag : IComponentData {}
public struct CharacterSpawnPointElement : IBufferElementData
{
    public float3 Position;
    public quaternion Rotation;
}
public struct CharacterSpawnPointsState : IComponentData
{
    public int NextIndex; 
}

public enum SpawnSelectMode : byte
{
    RoundRobin = 0,
    NetworkIdModulo = 1
}

public struct CharacterSpawnSelectMode : IComponentData 
{
    public SpawnSelectMode Value;     
}

public class CharacterSpawnPointsAuthoring : MonoBehaviour
{
    [Tooltip("Select Mode: RoundRobin or NetworkIdModulo")]
    public SpawnSelectMode selectMode = SpawnSelectMode.RoundRobin;
    public CampType campType = CampType.Alpha;
}
