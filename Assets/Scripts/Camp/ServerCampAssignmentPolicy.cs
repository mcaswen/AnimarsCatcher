using AnimarsCatcher.Mono.Global;

public static class ServerCampAssignmentPolicy
{
    // 根据连接的 NetworkId 和运行角色，为该连接分配阵营
    // Host 模式下：NetworkId == 1 的本地玩家是 Alpha，其余都是 Beta
    // DedicatedServer 模式下：奇数 Alpha，偶数 Beta
    public static CampType GetCampForConnection(int networkId)
    {
        if (NetRuntimeRole.IsHost)
        {
            return networkId == 1 ? CampType.Alpha : CampType.Beta;
        }

        return (networkId & 1) == 1 ? CampType.Alpha : CampType.Beta;
    }
}