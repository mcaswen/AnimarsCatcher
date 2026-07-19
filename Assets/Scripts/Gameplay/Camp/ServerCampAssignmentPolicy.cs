using AnimarsCatcher.Gameplay.Contracts;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 集中管理服务器为新连接分配阵营的确定性规则
    /// </summary>
    public static class ServerCampAssignmentPolicy
    {
        /// <summary>
        /// 根据连接编号和当前网络角色返回服务器权威阵营
        /// </summary>
        /// <param name="networkId">NetCode 分配的连接编号</param>
        /// <returns>该连接应使用的阵营</returns>
        public static CampType GetCampForConnection(int networkId)
        {
            return networkId == 1 ? CampType.Alpha : CampType.Beta;
        }
    }
}
