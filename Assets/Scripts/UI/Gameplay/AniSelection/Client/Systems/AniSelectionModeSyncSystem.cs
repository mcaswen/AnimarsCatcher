using Unity.Entities;
using AnimarsCatcher.Mono.Global;
using Unity.Collections;

/// <summary>
/// Mono 事件回调与 ECS 系统之间的临时模式同步上下文
/// </summary>
public static class AniSelectionModeSyncContext
{
    public static AniSelectionMode CurrentMode = AniSelectionMode.Picker;
    public static bool Dirty = false;
}

/// <summary>
/// 将 UI 发布的 Ani 选择模式写入客户端 ECS 单例
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct AniSelectionModeSyncSystem : ISystem
{

    /// <summary>
    /// 创建模式单例并订阅 Mono 层事件
    /// </summary>
    public void OnCreate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        if (!SystemAPI.TryGetSingleton<AniSelectionModeSingleton>(out var modeSingleton))
        {
            var singletonEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.SetName(singletonEntity, "AniSelectionModeSingleton");
            entityCommandBuffer.AddComponent(singletonEntity, new AniSelectionModeSingleton
            {
                Mode = AniSelectionMode.Picker
            });
        }

        AniSelectionModeSyncContext.CurrentMode = AniSelectionMode.Picker;
        AniSelectionModeSyncContext.Dirty = false;

        NetUIEventBridge.AniSelectionModeChanged.AddListener(OnSelectionModeChanged);

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

    /// <summary>
    /// 解除静态事件监听
    /// </summary>
    public void OnDestroy()
    {
        NetUIEventBridge.AniSelectionModeChanged.RemoveListener(OnSelectionModeChanged);
    }

    // 事件回调仅写入托管上下文 由 ECS 更新阶段正式提交
    private void OnSelectionModeChanged(AniSelectionModeChangedEvent eventData)
    {
        AniSelectionModeSyncContext.CurrentMode = eventData.Mode;
        AniSelectionModeSyncContext.Dirty = true;

        UnityEngine.Debug.Log($"[AniSelectionModeSyncSystem] On SelectionMode Changed: {AniSelectionModeSyncContext.CurrentMode}");
    }

    /// <summary>
    /// 在 Dirty 状态下更新或补建模式单例
    /// </summary>
    public void OnUpdate(ref SystemState state)
    {
        if (!AniSelectionModeSyncContext.Dirty)
            return;

        UnityEngine.Debug.Log($"[AniSelectionModeSyncSystem] Syncing AniSelectionMode: {AniSelectionModeSyncContext.CurrentMode}");
        AniSelectionModeSyncContext.Dirty = false;

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        if (!SystemAPI.TryGetSingletonRW<AniSelectionModeSingleton>(out var modeSingleton))
        {
            var singletonEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.SetName(singletonEntity, "AniSelectionModeSingleton");
            entityCommandBuffer.AddComponent(singletonEntity, new AniSelectionModeSingleton
            {
                Mode = AniSelectionModeSyncContext.CurrentMode
            });
        }
        else
        {
            modeSingleton.ValueRW.Mode = AniSelectionModeSyncContext.CurrentMode;
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}
