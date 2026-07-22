using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 将此组件添加到 SubScene 中的 GameObject，以配置 <see cref="NetDebug"/> 日志级别并启用数据包转储
    /// </summary>
    [HelpURL(Authoring.HelpURLs.NetCodeDebugConfigAuthoring)]
    public class NetCodeDebugConfigAuthoring : MonoBehaviour
    {
        /// <summary>
        /// NetCode 当前使用的调试级别
        /// </summary>
        public NetDebug.LogLevelType LogLevel = NetDebug.LogLevelType.Notify;
        /// <summary>
        /// 启用或禁用每个连接的数据包转储
        /// 启用后，会为每个连接创建一个文件，其中包含服务器发送或客户端接收的全部数据包
        /// 数据包转储会占用大量资源，主要且最好只用于调试复制问题
        /// </summary>
        public bool DumpPackets;
    }

    [BakingVersion("cmarastoni", 1)]
    class NetCodeDebugConfigAuthoringBaker : Baker<NetCodeDebugConfigAuthoring>
    {
        public override void Bake(NetCodeDebugConfigAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new NetCodeDebugConfig
            {
                LogLevel = authoring.LogLevel,
                DumpPackets = authoring.DumpPackets
            });
        }
    }
}
