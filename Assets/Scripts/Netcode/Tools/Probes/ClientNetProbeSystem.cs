using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>按固定间隔输出 Client World 的连接阶段状态</summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial struct ClientNetProbeSystem : ISystem
{
    private double _nextLogTime;

    /// <summary>每秒采样连接、NetworkId 和 InGame 状态</summary>
    /// <param name="state">系统状态</param>
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.Time.ElapsedTime < _nextLogTime) return;
        _nextLogTime = SystemAPI.Time.ElapsedTime + 1.0;

        bool hasConn = !SystemAPI.QueryBuilder().WithAll<NetworkStreamConnection>().Build().IsEmpty;
        bool hasId = SystemAPI.HasSingleton<NetworkId>();
        bool inGame = !SystemAPI.QueryBuilder().WithAll<NetworkId, NetworkStreamInGame>().Build().IsEmpty;
        
        Debug.Log($"[Client NetProbe] hasConn = {hasConn} hasId = {hasId} inGame = {inGame}");
    }
}
