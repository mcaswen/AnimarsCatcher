using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;

#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

/// <summary>
/// 在编辑器游戏场景中处理跳过大厅的调试 InGame 请求
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerGoInGameDebugSystem : ISystem
{
    /// <summary>
    /// 仅在编辑器中声明角色 Prefab 和出生点依赖
    /// </summary>
    /// <param name="state">系统状态</param>
    public void OnCreate(ref SystemState state)
    {
#if UNITY_EDITOR
        state.RequireForUpdate<CharacterGhostPrefab>();
        state.RequireForUpdate<CharacterSpawnPointsTag>();
#else
        state.Enabled = false;
#endif
    }

    /// <summary>
    /// 消费调试请求并由服务器创建角色及所有权关系
    /// </summary>
    /// <param name="state">系统状态</param>
    public void OnUpdate(ref SystemState state)
    {

#if !UNITY_EDITOR
        return;
#else
        // 场景限制防止调试协议介入正式大厅和菜单流程
        if (SceneManager.GetActiveScene().name != "SCN_GameLevel")
        {
            return;
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        var hasCharacterPrefab = SystemAPI.TryGetSingleton<CharacterGhostPrefab>(out var characterPrefab);
        var hasCameraPrefab    = SystemAPI.TryGetSingleton<CameraGhostPrefab>(out var cameraPrefab);

        // Prefab 注册未完成时保留请求，等待后续帧继续处理
        if (!hasCharacterPrefab || !hasCameraPrefab)
        {
            entityCommandBuffer.Playback(state.EntityManager);
            return;
        }

        // 调试协议仍由服务器选择阵营、出生点并授予角色所有权
        foreach (var (request, src, rpcEntity) in SystemAPI
                     .Query<RefRO<GoInGameRequest>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            var connectionEntity = src.ValueRO.SourceConnection;
            var connectionAspect = SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);

            var id = connectionAspect.Id;
            CampType camp = ServerCampAssignmentPolicy.GetCampForConnection(id);

            Debug.Log("[ServerGoInGameDebug] GoInGameRequest received.");

            foreach (var (spawnState, selectMode, groupCamp, points) in
                SystemAPI.Query<RefRW<CharacterSpawnPointsState>,
                                RefRO<CharacterSpawnSelectMode>,
                                RefRO<Camp>,
                                DynamicBuffer<CharacterSpawnPointElement>>())
            {

                if (groupCamp.ValueRO.Value != camp)
                    continue;

                // 先标记 InGame，允许该连接开始收发 Ghost 和输入快照
                connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

                if (connectionAspect.HasSpawned(ref state))
                {
                    Debug.Log("[ServerGoInGameDebug] Character already spawned for this connection, skip.");
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                // 出生点选择保持与正式开局流程一致
                bool spawnPointSelected = CharacterSpawnUtility.TrySelectCharacterSpawnPoint(
                    spawnState.ValueRW,
                    connectionAspect,
                    points,
                    selectMode.ValueRO.Value,
                    out var spawnPosition,
                    out var spawnRotation
                );

                // 角色创建只能在 Server World 执行
                var character = CharacterSpawnUtility.InstantiateAndInitialize(
                    ref entityCommandBuffer,
                    characterPrefab.Value,
                    id,
                    spawnPosition,
                    spawnRotation,
                    camp,
                    1f
                );

                // CommandTarget 决定输入流向，GhostOwner 决定客户端预测权限
                connectionAspect.SetCommandTarget(character, ref state, ref entityCommandBuffer);
                entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = id });
                entityCommandBuffer.AddComponent(character, new Camp { Value = camp });

                connectionAspect.MarkSpawned(ref entityCommandBuffer);

                entityCommandBuffer.DestroyEntity(rpcEntity);

                Debug.Log($"[ServerGoInGameDebug] Spawned character for connection {id} at {spawnPosition}");
            }
        }

        entityCommandBuffer.Playback(state.EntityManager);
#endif
    }
}
