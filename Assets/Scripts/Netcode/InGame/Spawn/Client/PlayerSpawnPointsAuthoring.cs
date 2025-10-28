using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public struct PlayerSpawnPointsTag : IComponentData {}
public struct PlayerSpawnPointElement : IBufferElementData
{
    public float3 Position;
    public quaternion Rotation;
}
public struct PlayerSpawnPointsState : IComponentData
{
    public int NextIndex; 
}

public enum SpawnSelectMode : byte
{
    RoundRobin = 0,
    NetworkIdModulo = 1
}

public struct PlayerSpawnSelectMode : IComponentData 
{
    public SpawnSelectMode Value;     
}

public class PlayerSpawnPointsAuthoring : MonoBehaviour
{
    [Tooltip("Select Mode: RoundRobin or NetworkIdModulo")]
    public SpawnSelectMode selectMode = SpawnSelectMode.RoundRobin;
}
