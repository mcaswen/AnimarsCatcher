using Unity.Entities;
using Unity.Burst;
using AnimarsCatcher.Mono.Global;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class PlayerInputLockFromUiSystem : SystemBase
{
    private Entity _lockStateEntity;

    protected override void OnCreate()
    {
        base.OnCreate();

        // 创建单例实体
        _lockStateEntity = EntityManager.CreateEntity(typeof(PlayerInputLockState));
        EntityManager.SetName(_lockStateEntity, "PlayerInputLockStateSingleton");

        EntityManager.SetComponentData(_lockStateEntity, new PlayerInputLockState
        {
            LockCount = 0
        });

        // 订阅 UI 事件
        NetUIEventBridge.UIPanelInputToggleEvent.AddListener(OnUIPanelInputToggle);
    }

    protected override void OnDestroy()
    {
        // 取消订阅，防止退出 Play 时报 MissingReference
        NetUIEventBridge.UIPanelInputToggleEvent.RemoveListener(OnUIPanelInputToggle);
        base.OnDestroy();
    }

    private void OnUIPanelInputToggle(UIPanelInputToggleEvent data)
    {
        if (_lockStateEntity == Entity.Null || !EntityManager.Exists(_lockStateEntity))
            return;

        var state = EntityManager.GetComponentData<PlayerInputLockState>(_lockStateEntity);

        state.LockCount += data.Delta;
        if (state.LockCount < 0)
            state.LockCount = 0; // 防御：避免调用不配对，锁计数变负数

        EntityManager.SetComponentData(_lockStateEntity, state);
    }

    protected override void OnUpdate()
    {
        // 留空，事件驱动
    }
}
