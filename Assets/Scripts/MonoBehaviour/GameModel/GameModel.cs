using UnityEngine;
using AnimarsCatcher.Mono.Global;
using AnimarsCatcher.Mono.Utilities;
using System.Collections.Generic;
using System;

namespace AnimarsCatcher.Mono
{
    [System.Serializable]
    public class GameModel
    {
        public ReactiveProperty<int> Day = new();
        public ReactiveProperty<int> PickerAniCount = new();
        public ReactiveProperty<int> BlasterAniCount = new();
        public ReactiveProperty<int> FoodSum = new();
        public ReactiveProperty<int> CrystalSum = new();

        private List<Action> _unsubscribeActions = new List<Action>();

        private int _blueprintCount;
        public int BlueprintCount
        {
            get { return _blueprintCount; }
            set
            {
                if (value != _blueprintCount)
                {
                    _blueprintCount = value;
                    EventBus.Instance.Publish(new BlueprintCountUpdatedEventData() { BlueprintCount = _blueprintCount });
                }
            }
        }

        public ReactiveProperty<int> InTeamPickerAniCount = new ReactiveProperty<int>();
        public ReactiveProperty<int> InTeamBlasterAniCount = new ReactiveProperty<int>();

        public GameModel()
        {
            EventBus.Instance?.Subscribe<BlueprintCollectedEventData>(eventData =>
            {
                BlueprintCount += 1;
            });

            EventBus.Instance?.Subscribe<FoodCollectedEventData>(eventData =>
            {
                FoodSum.Value += eventData.ResourceCount;
            });

            EventBus.Instance?.Subscribe<CrystalCollectedEventData>(eventData =>
            {
                CrystalSum.Value += eventData.ResourceCount;
            });
        }

        public bool HasSaveData()
        {
            return PlayerPrefs.HasKey(nameof(Day));
        }

        public void Load()
        {
            Day.Value = PlayerPrefs.GetInt(nameof(Day));
            PickerAniCount.Value = PlayerPrefs.GetInt(nameof(PickerAniCount));
            BlasterAniCount.Value = PlayerPrefs.GetInt(nameof(BlasterAniCount));
            FoodSum.Value = PlayerPrefs.GetInt(nameof(FoodSum));
            CrystalSum.Value = PlayerPrefs.GetInt(nameof(CrystalSum));
            BlueprintCount = PlayerPrefs.GetInt(nameof(BlueprintCount));

            InTeamPickerAniCount.Value = 0;
            InTeamBlasterAniCount.Value = 0;
        }

        public void Save()
        {
            PlayerPrefs.SetInt(nameof(Day), Day.Value);
            PlayerPrefs.SetInt(nameof(PickerAniCount), PickerAniCount.Value);
            PlayerPrefs.SetInt(nameof(BlasterAniCount), BlasterAniCount.Value);
            PlayerPrefs.SetInt(nameof(FoodSum), FoodSum.Value);
            PlayerPrefs.SetInt(nameof(CrystalSum), CrystalSum.Value);
            PlayerPrefs.SetInt(nameof(BlueprintCount), BlueprintCount);
        }

        // ~GameModel()
        // {
        //     foreach (var unsubscribe in _unsubscribeActions)
        //     {
        //         subscription?.Dispose();
        //     }
        //     _subscriptions.Clear();
        // }
    }
}