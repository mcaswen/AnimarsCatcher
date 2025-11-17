using System;

namespace AnimarsCatcher.Mono.Global
{
    public interface IEventData { }
    public struct BlueprintCollectedEventData : IEventData
    { }

    public struct FoodCollectedEventData : IEventData
    {
        public int ResourceCount;

        public FoodCollectedEventData(int resourceCount)
        {
            ResourceCount = resourceCount;
        }
    }

    public struct CrystalCollectedEventData : IEventData
    {
        public int ResourceCount;

        public CrystalCollectedEventData(int resourceCount)
        {
            ResourceCount = resourceCount;
        }
    }

    public struct BlueprintCountUpdatedEventData : IEventData
    {
        public int BlueprintCount;

        public BlueprintCountUpdatedEventData(int blueprintCount)
        {
            BlueprintCount = blueprintCount;
        }
    }

    public struct LevelDayEndedEventData : IEventData
    { }

    public struct LevelDayStartedEventData : IEventData
    {
        public int SpawningBlasterAniCount, SpawningPickerAniCount;

        public LevelDayStartedEventData(int spawningBlasterAniCount, int spawningPickerAniCount)
        {
            SpawningBlasterAniCount = spawningBlasterAniCount;
            SpawningPickerAniCount = spawningPickerAniCount;
        }
    }

    public struct GameRoomCreatedEventData : IEventData
    { }

    public struct JoinGameRoomRequestEventData : IEventData
    { }

}