namespace AnimarsCatcher.Networking
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.NetCode;
    using Unity.Collections;
    using AnimarsCatcher.Player;

    /// <summary>
    /// 在客户端将本地连接绑定到其预测角色和主相机
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup), OrderFirst = true)]
    public partial struct EnsureClientCommandTargetSystem : ISystem
    {
        private bool _cameraIsBinded;
        private bool _characterIsBinded;

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var connection))
                return;

            // 两项关系完成后停止扫描 Ghost，避免输入组每帧遍历实体
            if (_cameraIsBinded && _characterIsBinded) return;

            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 连接可能早于角色 Ghost 到达，先保证 CommandTarget 组件存在
            if (!state.EntityManager.HasComponent<CommandTarget>(connection))
                entityCommandBuffer.AddComponent(connection, new CommandTarget { targetEntity = Entity.Null });

            var commandTarget = SystemAPI.GetComponent<CommandTarget>(connection);
            if (commandTarget.targetEntity == Entity.Null)
            {
                var localNetworkId = SystemAPI.GetComponent<NetworkId>(connection).Value;

                // 只有归属本地 NetworkId 的预测 Ghost 可以接收该连接的输入命令
                foreach (var (owner, characterEntity)
                         in SystemAPI.Query<RefRO<GhostOwner>>().WithAll<CharacterTag, PredictedGhost>().WithEntityAccess())
                {
                    if (owner.ValueRO.NetworkId == localNetworkId)
                    {
                        entityCommandBuffer.SetComponent(connection, new CommandTarget { targetEntity = characterEntity });
                        if (SystemAPI.TryGetSingleton<ThirdPersonPlayerControl>(out var playerControl))
                        {
                            playerControl.ControlledCharacter = characterEntity;
                            SystemAPI.SetSingleton(playerControl);

                            _characterIsBinded = true;
                        }
                        break;
                    }
                }

                // 主相机实体属于客户端表现状态，不参与服务器权限判定
                foreach (var (camera, cameraEntity) in SystemAPI.Query<RefRO<MainEntityCamera>>()
                        .WithAll<MainEntityCamera>()
                        .WithEntityAccess())
                {
                    if (SystemAPI.TryGetSingleton<ThirdPersonPlayerControl>(out var playerControl))
                    {
                        UnityEngine.Debug.Log($"绑定相机实体 {cameraEntity} 给本地玩家");
                        playerControl.ControlledCamera = cameraEntity;
                        SystemAPI.SetSingleton(playerControl);

                        _cameraIsBinded = true;
                    }
                    break;
                }
            }
            entityCommandBuffer.Playback(state.EntityManager);
        }
    }
}
