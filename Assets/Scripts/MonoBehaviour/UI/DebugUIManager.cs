using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.UI
{
    public class DebugUIManager : MonoBehaviour
    {
        public Button AddCrystalButton;
        public Button AddFoodButton;

        private void Awake()
        {
            AddCrystalButton.onClick.AddListener(() =>
            {
                EventBus.Instance.Publish(new CrystalCollectedEventData(2));
            });

            AddFoodButton.onClick.AddListener(() =>
            {
                EventBus.Instance.Publish(new FoodCollectedEventData(2));
            });
        }
    }
}