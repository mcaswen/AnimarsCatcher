using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode
{
    /// <summary>
    /// 由 <see cref="DriverMigrationSystem.StoreWorld"/> 返回的 Singleton Entity
    /// 可用于把之前保存的 Driver 状态加载到另一个 World
    /// </summary>
    public struct MigrationTicket : IComponentData
    {
        /// <summary>
        /// Ticket 的唯一值
        /// </summary>
        public int Value;
    }

    /// <summary>
    /// 在把内部 Transport 连接转移到另一个 World 期间，用于临时保持这些连接存活的系统
    /// 例如，可以依靠 DriverMigrationSystem 在 Lobby World 与 Game World 之间复用相同连接
    /// </summary>
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    public partial class DriverMigrationSystem : SystemBase
    {
        /// <summary>
        /// Driver 迁移到新 World 时，恢复全部 <see cref="NetworkStreamConnection"/> 所需的最小内部状态
        /// </summary>
        internal struct DriverStoreState
        {
            /// <summary>
            /// <see cref="NetworkDriverStore"/> 的副本
            /// </summary>
            public NetworkDriverStore DriverStore;
            /// <summary>
            /// 没有可复用 Network ID 时，应分配给新入站连接的下一个 Network ID
            /// </summary>
            public int NextId;
            /// <summary>
            /// 可供入站连接复用的 Network ID 列表
            /// </summary>
            public NativeArray<int> FreeList;
            /// <summary>
            /// 最近用于连接服务器或监听入站连接的 <see cref="NetworkEndpoint"/>
            /// </summary>
            public NetworkEndpoint LastEp;
            /// <summary>
            /// 销毁所有已分配资源
            /// </summary>
            /// <returns></returns>
            public void Dispose()
            {
                DriverStore.Dispose();
                if (FreeList.IsCreated)
                    FreeList.Dispose();
            }
        }

        /// <summary>
        /// 包含 Driver 状态及其临时转移到的 Backup World
        /// </summary>
        internal struct WorldState
        {
            /// <summary>
            /// Driver 的内部状态
            /// </summary>
            public DriverStoreState DriverStoreState;
            /// <summary>
            /// 保存 Driver 状态时构造的临时 Backup World，参见 <see cref="DriverMigrationSystem.StoreWorld"/>
            /// </summary>
            public World BackupWorld;
        }

        private Dictionary<int, WorldState> driverMap;
        private int m_TicketCounter;

        protected override void OnCreate()
        {
            driverMap = new Dictionary<int, WorldState>();
            m_TicketCounter = 0;
        }

        /// <summary>
        /// 保存指定 World 的 NetworkDriver 与 Connection 数据以供迁移
        /// </summary>
        /// <param name="sourceWorld">要保存的 World</param>
        /// <remarks>只有具有 `NetworkStreamConnection` 类型的 Entity 会迁移到新 World</remarks>
        /// <returns>用于获取已保存 NetworkDriver 数据的 Ticket</returns>
        public int StoreWorld(World sourceWorld)
        {
            var ticket = ++m_TicketCounter;

            if (driverMap.ContainsKey(ticket))
                throw new ApplicationException("Unhandled error state, the ticket already exists in driver map.");

            driverMap.Add(ticket, default);

            using var driverSingletonQuery = sourceWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
            ref var driverSingleton = ref driverSingletonQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            driverSingletonQuery.CompleteDependency();
            Store(driverSingleton.StoreMigrationState(), ticket);

            using var filter = sourceWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
            var backupWorld = new World(sourceWorld.Name, sourceWorld.Flags);

            backupWorld.EntityManager.MoveEntitiesFrom(sourceWorld.EntityManager, filter);

            var worldState = driverMap[ticket];
            worldState.BackupWorld = backupWorld;

            driverMap[ticket] = worldState;
            return ticket;
        }

        /// <summary>
        /// 把已保存的 NetworkDriver 与 Connection 数据加载到新建或现有 World
        /// </summary>
        /// <param name="ticket">已保存 World 对应的 Ticket</param>
        /// <param name="newWorld">可选的目标 World</param>
        /// <returns>已准备好添加系统的 World</returns>
        /// <remarks>必须在目标 World 上的任何系统初始化前调用此函数</remarks>
        /// <exception cref="ArgumentException">传入无效 World 时抛出，仅支持 NetCode World</exception>
        public World LoadWorld(int ticket, World newWorld = null)
        {
            if (driverMap.TryGetValue(ticket, out var driver))
            {
                if (!driver.BackupWorld.IsCreated)
                    throw new ApplicationException("The driver contains no valid BackupWorld to migrate from.");

                if (newWorld == null)
                    newWorld = driver.BackupWorld;
                else
                {
                    //Debug.Assert(null == newWorld.GetExistingSystem<NetworkStreamReceiveSystem>());

                    var filter = driver.BackupWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
                    newWorld.EntityManager.MoveEntitiesFrom(driver.BackupWorld.EntityManager, filter);
                    driver.BackupWorld.Dispose();
                }

                var e = newWorld.EntityManager.CreateEntity();
                newWorld.EntityManager.AddComponentData(e, new MigrationTicket {Value = ticket});

                return newWorld;
            }
            throw new ArgumentException("You can only migrate a world created by netcode. Make sure you are creating your worlds correctly.");
        }

        internal DriverStoreState Load(int ticket)
        {
            if (driverMap.TryGetValue(ticket, out var driver))
            {
                driverMap.Remove(ticket);
                return driver.DriverStoreState;
            }
            throw new ArgumentException("You can only migrate a world created by netcode. Make sure you are creating your worlds correctly.");
        }

        internal void Store(DriverStoreState state, int ticket)
        {
            Debug.Assert(driverMap.ContainsKey(ticket));
            var worldState = driverMap[ticket];

            worldState.DriverStoreState = state;

            driverMap[ticket] = worldState;
        }


        protected override void OnDestroy()
        {
            foreach (var keyValue in driverMap)
            {
                var state = keyValue.Value;
                state.DriverStoreState.Dispose();
                if (state.BackupWorld.IsCreated)
                    state.BackupWorld.Dispose();
            }
        }

        protected override void OnUpdate()
        {
        }
    }
}
