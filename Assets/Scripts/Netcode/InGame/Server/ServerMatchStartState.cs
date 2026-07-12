using Unity.Collections;
using Unity.Entities;

/// <summary>保存服务器开局协议各阶段的单例状态</summary>
public struct ServerMatchStartState : IComponentData
{
    /// <summary>服务器要求所有客户端加载的场景</summary>
    public FixedString64Bytes SceneName;
    /// <summary>是否已收到有效开局请求</summary>
    public byte MatchStartRequested;   
    /// <summary>是否已向现有连接广播开局 RPC</summary>
    public byte ClientStartRpcSent;   
    /// <summary>是否至少成功创建一个角色</summary>
    public byte CharactersSpawned;    
}
