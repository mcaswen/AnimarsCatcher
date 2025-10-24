using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

// 服务器连接情况探针
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct NetProbe_Server : ISystem
{
    double _next;
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.Time.ElapsedTime < _next) return;
        _next = SystemAPI.Time.ElapsedTime + 1;

        int conns = 0, withId = 0, inGame = 0;
        
        foreach (var _ in SystemAPI.Query<RefRO<NetworkStreamConnection>>()) conns++;
        foreach (var _ in SystemAPI.Query<RefRO<NetworkId>>()) withId++;
        foreach (var _ in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>()) inGame++;

        Debug.Log($"[Server] conns = {conns} withId = {withId} inGame = {inGame}");
    }
}
