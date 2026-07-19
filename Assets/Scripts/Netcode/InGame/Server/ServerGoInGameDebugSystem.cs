namespace AnimarsCatcher.Networking
{
    using AnimarsCatcher.Gameplay;
    using AnimarsCatcher.Gameplay.Contracts;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;
    using UnityEngine;
    using UnityEngine.SceneManagement;

    /// <summary>
    /// 在编辑器游戏场景中处理跳过大厅的调试 InGame 请求
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RpcSystem))]
    public partial struct ServerGoInGameDebugSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (!NetworkPlayModeConfiguration.HasEditorOverride)
            {
                state.Enabled = false;
                return;
            }

            state.RequireForUpdate<CharacterGhostPrefab>();
            state.RequireForUpdate<CharacterSpawnPointsTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 场景限制防止调试协议介入正式大厅和菜单流程
            if (SceneManager.GetActiveScene().name != "SCN_GameLevel")
            {
                return;
            }

            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            bool hasCharacterPrefab =
                SystemAPI.TryGetSingleton(out CharacterGhostPrefab characterPrefab);
            bool hasCameraPrefab =
                SystemAPI.TryGetSingleton(out CameraGhostPrefab cameraPrefab);

            // Prefab 注册未完成时保留请求，等待后续帧继续处理
            if (!hasCharacterPrefab || !hasCameraPrefab)
            {
                entityCommandBuffer.Playback(state.EntityManager);
                return;
            }

            // 调试协议仍由服务端选择阵营、出生点并授予角色所有权
            foreach (var (request, source, rpcEntity) in SystemAPI
                         .Query<RefRO<GoInGameRequest>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                Entity connectionEntity = source.ValueRO.SourceConnection;
                ServerGetConnectionAspect connectionAspect =
                    SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);
                int networkId = connectionAspect.Id;
                CampType camp = ServerCampAssignmentPolicy.GetCampForConnection(networkId);

                Debug.Log("[ServerGoInGameDebug] GoInGameRequest received");

                foreach (var (spawnState, selectMode, groupCamp, points) in
                         SystemAPI.Query<RefRW<CharacterSpawnPointsState>,
                             RefRO<CharacterSpawnSelectMode>,
                             RefRO<Camp>,
                             DynamicBuffer<CharacterSpawnPointElement>>())
                {
                    if (groupCamp.ValueRO.Value != camp)
                    {
                        continue;
                    }

                    // 先标记 InGame，允许连接开始收发 Ghost 和输入快照
                    connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

                    if (connectionAspect.HasSpawned(ref state))
                    {
                        Debug.Log(
                            "[ServerGoInGameDebug] Character already spawned for this connection, skip");
                        entityCommandBuffer.DestroyEntity(rpcEntity);
                        continue;
                    }

                    // 出生点选择保持与正式开局流程一致
                    CharacterSpawnUtility.TrySelectCharacterSpawnPoint(
                        spawnState.ValueRW,
                        connectionAspect,
                        points,
                        selectMode.ValueRO.Value,
                        out float3 spawnPosition,
                        out quaternion spawnRotation);

                    // 角色创建只能在 Server World 执行
                    Entity character = CharacterSpawnUtility.InstantiateAndInitialize(
                        ref entityCommandBuffer,
                        characterPrefab.Value,
                        networkId,
                        spawnPosition,
                        spawnRotation,
                        camp,
                        1f);

                    // CommandTarget 决定输入流向，GhostOwner 决定客户端预测权限
                    connectionAspect.SetCommandTarget(
                        character,
                        ref state,
                        ref entityCommandBuffer);
                    entityCommandBuffer.AddComponent(
                        character,
                        new GhostOwner { NetworkId = networkId });
                    entityCommandBuffer.AddComponent(character, new Camp { Value = camp });

                    connectionAspect.MarkSpawned(ref entityCommandBuffer);
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    Debug.Log(
                        $"[ServerGoInGameDebug] Spawned character for connection {networkId} at {spawnPosition}");
                }
            }

            entityCommandBuffer.Playback(state.EntityManager);
        }
    }
}
