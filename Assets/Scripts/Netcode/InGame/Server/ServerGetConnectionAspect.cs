namespace AnimarsCatcher.Networking
{
    #pragma warning disable CS0618
    using Unity.Entities;
    using Unity.NetCode;

    /// <summary>
    /// 标记服务器已为连接创建角色，防止重复生成
    /// </summary>
    public struct PlayerSpawnedTag : IComponentData {}

    /// <summary>
    /// 封装服务器对连接 Entity 的 InGame、生成去重和 CommandTarget 操作
    /// </summary>
    public readonly partial struct ServerGetConnectionAspect : IAspect
    {
        public readonly Entity Self;

        readonly RefRO<NetworkId> _networkId;

        public int Id => _networkId.ValueRO.Value;

        /// <summary>
        /// 确保连接带有 NetworkStreamInGame 标记
        /// </summary>
        /// <param name="state">服务器系统状态</param>
        /// <param name="entityCommandBuffer">延迟结构变更命令缓冲区</param>
        public void EnsureInGame(ref SystemState state, ref EntityCommandBuffer entityCommandBuffer)
        {
            if (!state.EntityManager.HasComponent<NetworkStreamInGame>(Self))
            {
                entityCommandBuffer.AddComponent<NetworkStreamInGame>(Self);
            }
        }

        /// <summary>
        /// 判断服务器是否已为该连接创建角色
        /// </summary>
        /// <param name="state">服务器系统状态</param>
        /// <returns>连接是否已完成角色创建</returns>
        public bool HasSpawned(ref SystemState state)
        {
            return state.EntityManager.HasComponent<PlayerSpawnedTag>(Self);
        }

        /// <summary>
        /// 记录该连接已完成角色创建
        /// </summary>
        /// <param name="entityCommandBuffer">延迟结构变更命令缓冲区</param>
        public void MarkSpawned(ref EntityCommandBuffer entityCommandBuffer)
        {
            entityCommandBuffer.AddComponent<PlayerSpawnedTag>(Self);
        }

        /// <summary>
        /// 将连接的输入命令目标设置为服务器创建的角色
        /// </summary>
        /// <param name="character">该连接拥有的角色 Entity</param>
        /// <param name="state">服务器系统状态</param>
        /// <param name="entityCommandBuffer">延迟结构变更命令缓冲区</param>
        public void SetCommandTarget(Entity character, ref SystemState state, ref EntityCommandBuffer entityCommandBuffer)
        {
            if (state.EntityManager.HasComponent<CommandTarget>(Self))
            {
                entityCommandBuffer.SetComponent(Self, new CommandTarget { targetEntity = character });
            }
            else
            {
                entityCommandBuffer.AddComponent(Self, new CommandTarget { targetEntity = character });
            }
        }


    }
}
