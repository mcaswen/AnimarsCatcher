using Unity.Entities;
using AnimarsCatcher.Player;

namespace AnimarsCatcher.Presentation.InputLock
{
/// <summary>
/// 在客户端根据 UI 面板占用情况维护玩法输入锁
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class ClientPlayerInputLockFromUISystem : SystemBase
{
    private Entity _lockStateEntity;

    protected override void OnCreate()
    {
        base.OnCreate();

        // 使用计数而非布尔值，允许多个面板独立申请和释放输入锁
        _lockStateEntity = EntityManager.CreateEntity(typeof(PlayerInputLockState));
        EntityManager.SetName(_lockStateEntity, "PlayerInputLockStateSingleton");

        EntityManager.SetComponentData(_lockStateEntity, new PlayerInputLockState
        {
            LockCount = 0
        });

        // UI 通过事件传递增量，避免表现层直接依赖 ECS 世界
        UIInputEvents.LockDeltaChanged.AddListener(OnInputLockDeltaChanged);
    }

    protected override void OnDestroy()
    {
        // 系统销毁时必须退订，避免退出 Play 后事件仍持有托管回调
        UIInputEvents.LockDeltaChanged.RemoveListener(OnInputLockDeltaChanged);
        base.OnDestroy();
    }

    private void OnInputLockDeltaChanged(UIInputLockDelta data)
    {
        if (_lockStateEntity == Entity.Null || !EntityManager.Exists(_lockStateEntity))
            return;

        var state = EntityManager.GetComponentData<PlayerInputLockState>(_lockStateEntity);

        state.LockCount += data.Value;
        if (state.LockCount < 0)
            state.LockCount = 0; // 防御未配对的释放请求，避免锁计数变为负数

        EntityManager.SetComponentData(_lockStateEntity, state);
    }

    protected override void OnUpdate() { }
}

}
