using System;
using System.Collections.Generic;

namespace AnimarsCatcher.Mono
{
    [Serializable]
    public class AreaData
    {
        public int FoodNum;
        public int CrystalNum;
        public List<int> Area;
    }

    [Serializable]
    public class DetailedLevelData
    {
        public int Day;
        public List<AreaData> Resources;
        public int LevelTime;
    }

    public class DetailedLevelInfo
    {
        public List<DetailedLevelData> LevelDatas;
        public int PickerAniFoodCostCount;
        public int PickerAniCrystalCostCount;
        public int BlasterAniFoodCostCount;
        public int BlasterAniCrystalCostCount;
    }
}
