using Unity.Entities;
using Unity.Collections;

namespace AnimarsCatcher.Presentation.Selection
{
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
    public partial struct ClientAniSelectionModeSyncSystem : ISystem
    {

        public void OnCreate(ref SystemState state)
        {
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            if (!SystemAPI.TryGetSingleton<AniSelectionModeState>(out var modeSingleton))
            {
                var singletonEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.SetName(singletonEntity, "AniSelectionModeState");
                entityCommandBuffer.AddComponent(singletonEntity, new AniSelectionModeState
                {
                    Mode = AniSelectionMode.Picker
                });
            }

            AniSelectionModeSyncContext.CurrentMode = AniSelectionMode.Picker;
            AniSelectionModeSyncContext.Dirty = false;

            AniSelectionEvents.ModeChanged.AddListener(OnSelectionModeChanged);

            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
        }

        public void OnDestroy()
        {
            AniSelectionEvents.ModeChanged.RemoveListener(OnSelectionModeChanged);
        }

            // 事件回调只写入托管上下文，再由 ECS 更新阶段同步到单例
        private void OnSelectionModeChanged(AniSelectionModeChangedEvent eventData)
        {
            AniSelectionModeSyncContext.CurrentMode = eventData.Mode;
            AniSelectionModeSyncContext.Dirty = true;

            UnityEngine.Debug.Log($"[ClientAniSelectionModeSyncSystem] On SelectionMode Changed: {AniSelectionModeSyncContext.CurrentMode}");
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!AniSelectionModeSyncContext.Dirty)
                return;

            UnityEngine.Debug.Log($"[ClientAniSelectionModeSyncSystem] Syncing AniSelectionMode: {AniSelectionModeSyncContext.CurrentMode}");
            AniSelectionModeSyncContext.Dirty = false;

            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            if (!SystemAPI.TryGetSingletonRW<AniSelectionModeState>(out var modeSingleton))
            {
                var singletonEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.SetName(singletonEntity, "AniSelectionModeState");
                entityCommandBuffer.AddComponent(singletonEntity, new AniSelectionModeState
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
}
