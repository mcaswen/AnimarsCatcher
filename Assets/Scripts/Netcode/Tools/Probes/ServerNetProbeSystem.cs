using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>按固定间隔输出 Server World 的连接阶段计数</summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerNetProbeSystem : ISystem
{
    private double _nextLogTime;

    /// <summary>每秒统计连接、NetworkId 和 InGame 实体数量</summary>
    /// <param name="state">系统状态</param>
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.Time.ElapsedTime < _nextLogTime) return;
        _nextLogTime = SystemAPI.Time.ElapsedTime + 1;

        int conns = 0, withId = 0, inGame = 0;
        
        foreach (var _ in SystemAPI.Query<RefRO<NetworkStreamConnection>>()) conns++;
        foreach (var _ in SystemAPI.Query<RefRO<NetworkId>>()) withId++;
        foreach (var _ in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>()) inGame++;

        Debug.Log($"[Server NetProbe] conns = {conns} withId = {withId} inGame = {inGame}");
    }
}
