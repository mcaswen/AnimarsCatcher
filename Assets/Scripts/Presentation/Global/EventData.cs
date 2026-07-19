using System;

namespace AnimarsCatcher.Presentation.Global
{
    /// <summary>
    /// 标记可通过 EventBus 发布的数据类型
    /// </summary>
    public interface IEventData { }

    /// <summary>
    /// 蓝图被收集时发布的无负载事件
    /// </summary>
    public struct BlueprintCollectedEventData : IEventData
    { }

    /// <summary>
    /// 食物资源数量变化事件
    /// </summary>
    public struct FoodCollectedEventData : IEventData
    {
        public int ResourceCount;

        public FoodCollectedEventData(int resourceCount)
        {
            ResourceCount = resourceCount;
        }
    }

    /// <summary>
    /// 水晶资源数量变化事件
    /// </summary>
    public struct CrystalCollectedEventData : IEventData
    {
        public int ResourceCount;

        public CrystalCollectedEventData(int resourceCount)
        {
            ResourceCount = resourceCount;
        }
    }

    /// <summary>
    /// 蓝图总数刷新事件
    /// </summary>
    public struct BlueprintCountUpdatedEventData : IEventData
    {
        public int BlueprintCount;

        public BlueprintCountUpdatedEventData(int blueprintCount)
        {
            BlueprintCount = blueprintCount;
        }
    }

    /// <summary>
    /// 当前关卡日结束事件
    /// </summary>
    public struct LevelDayEndedEventData : IEventData
    { }

    /// <summary>
    /// 新关卡日开始及两类 Ani 生成数量
    /// </summary>
    public struct LevelDayStartedEventData : IEventData
    {
        public int SpawningBlasterAniCount, SpawningPickerAniCount;

        public LevelDayStartedEventData(int spawningBlasterAniCount, int spawningPickerAniCount)
        {
            SpawningBlasterAniCount = spawningBlasterAniCount;
            SpawningPickerAniCount = spawningPickerAniCount;
        }
    }

    /// <summary>
    /// 房间创建完成事件
    /// </summary>
    public struct GameRoomCreatedEventData : IEventData
    { }

    /// <summary>
    /// 请求加入游戏房间的事件
    /// </summary>
    public struct JoinGameRoomRequestEventData : IEventData
    { }

}
