using System.Runtime.InteropServices;
using Unity.Entities;
using Unity.Physics;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于配置 NetCode 延迟补偿系统的 Singleton Entity
    /// 如果此 Singleton 不存在，PhysicsWorldHistory 系统将不会运行
    /// 若要在预测系统中使用 PhysicsWorldHistory，客户端和服务器 World 中都必须存在此配置，
    /// 但客户端 World 的 HistorySize 可以与服务器不同，通常设为 1 即可
    /// </summary>
    public struct LagCompensationConfig : IComponentData
    {
        /// <summary>
        /// 在服务器上备份的 Physics World 状态数量
        /// 不能超过 <see cref="PhysicsWorldHistory.RawHistoryBufferMaxCapacity"/> 定义的最大容量
        /// 保持为 0 时使用默认值 16
        /// </summary>
        /// <remarks>
        /// 必须是 2 的幂，才能在 <see cref="NetworkTime.ServerTick"/> 越过 uint 最大值时，
        /// 让环形缓冲区返回正确结果
        /// </remarks>
        public int ServerHistorySize;
        /// <summary>
        /// 在客户端上备份的 Physics World 状态数量
        /// 不能超过 <see cref="PhysicsWorldHistory.RawHistoryBufferMaxCapacity"/> 定义的最大容量，
        /// 但通常只需要约 4 个，以便客户端用历史记录检查自己的射击
        /// 设为 0 会禁用客户端 Physics 历史记录
        /// 客户端的默认历史长度为 1
        /// </summary>
        /// <remarks>
        /// 必须为 0，表示关闭，否则必须是 2 的幂，才能在 <see cref="NetworkTime.ServerTick"/>
        /// 越过 uint 最大值时让环形缓冲区返回正确结果
        /// </remarks>
        public int ClientHistorySize;
        /// <summary>
        /// 决定 NetCode 调用 <see cref="CollisionWorld.Clone()"/> 时是否深拷贝动态 Collider
        /// 若希望 <see cref="PhysicsWorldHistory"/> 对动态 Entity 的历史查询返回准确结果，请设为 true
        /// </summary>
        /// <remarks>
        /// 还需注意，从 NetCode 的角度看，查询未深拷贝的 Entity 属于未定义行为
        /// 此处唯一保证是 Physics 查询本身不会抛出异常，因为安全处理该流程属于 Physics 的职责
        /// </remarks>
        [MarshalAs(UnmanagedType.U1)]
        public bool DeepCopyDynamicColliders;
        /// <summary>
        /// 决定 NetCode 调用 <see cref="CollisionWorld.Clone()"/> 时是否深拷贝静态 Collider
        /// 若希望 <see cref="PhysicsWorldHistory"/> 对静态 Entity 的历史查询返回准确结果，请设为 true
        /// 仅当静态 Collider 信息发生变化，包括几何形状变化，并导致碰撞检测失效时才需要启用，
        /// 这应当属于少见的异常情况
        /// </summary>
        /// <remarks>
        /// 对大型 World，最好避免复制静态几何体
        /// 可改为执行两次查询，先通过 Layer 查询当前静态几何体，
        /// 再使用该碰撞结果设置动态 Collider 查询的最大投射距离
        /// <br/><br/>如果游戏中的静态几何体偶尔会变化，例如砍倒一棵树，
        /// 可通过 <see cref="PhysicsWorldHistorySingleton.DeepCopyRigidBodyCollidersWhitelist"/> 手动复制这些刚体的 Collider
        /// <br/><br/>还需注意，从 NetCode 的角度看，查询未深拷贝的 Entity 属于未定义行为
        /// 此处唯一保证是 Physics 查询本身不会抛出异常，因为安全处理该流程属于 Physics 的职责
        /// </remarks>
        [MarshalAs(UnmanagedType.U1)]
        public bool DeepCopyStaticColliders;
    }
}
