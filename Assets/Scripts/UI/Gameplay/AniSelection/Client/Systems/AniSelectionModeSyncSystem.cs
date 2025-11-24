using Unity.Entities;
using AnimarsCatcher.Mono.Global;
using Unity.Collections;

public static class AniSelectionModeSyncContext
{
    public static AniSelectionMode CurrentMode = AniSelectionMode.Picker;
    public static bool Dirty = false;
}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct AniSelectionModeSyncSystem : ISystem
{

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

    public void OnDestroy()
    {
        NetUIEventBridge.AniSelectionModeChanged.RemoveListener(OnSelectionModeChanged);
    }

    private void OnSelectionModeChanged(AniSelectionModeChangedEvent eventData)
    {
        AniSelectionModeSyncContext.CurrentMode = eventData.Mode;
        AniSelectionModeSyncContext.Dirty = true;

        UnityEngine.Debug.Log($"[AniSelectionModeSyncSystem] On SelectionMode Changed: {AniSelectionModeSyncContext.CurrentMode}");
    }

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
