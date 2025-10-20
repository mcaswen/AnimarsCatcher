using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace AnimarsCatcher
{
    public enum AchievementType
    {
        SpecialItemCollected = 0,
        FirstPickerAniCollected = 1
    }
    public class AchievementManager : MonoBehaviour
    {
        public static AchievementManager Instance { get; private set; }
        private int _SpecialItemCollected = 0;

        [SerializeField] private List<Image> AchievementIcons = new List<Image>();
        
        [SerializeField] private float _IconMoveSpeed = 200f;
        [SerializeField] private float _IconDisplayDelaySeconds = 3f;
        [SerializeField] private int _SpecialItemAchievementThreshold = 6;
        private bool _HasFirstPickerAniCollected = false;


        [SerializeField] private float IconDestinationY = -420f;

        private IEnumerator _DisplayIconCoroutine;

        void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(this);
            }
        }

        void Start()
        {
            foreach (var icon in AchievementIcons)
            {
                if (icon != null)
                {
                    icon.enabled = false;
                }
            }
        }

        public void RecordSpecialItemCollected()
        {
            _SpecialItemCollected++;
            Debug.Log($"Special items collected: {_SpecialItemCollected}");

            if (_SpecialItemCollected == _SpecialItemAchievementThreshold)
            {
                UnlockAchievement(AchievementType.SpecialItemCollected);
            }

        }

        public void RecordFirstPickerAniCollected()
        {
            if (_HasFirstPickerAniCollected) return;

            Debug.Log($"First Ani collected");

            _HasFirstPickerAniCollected = true;
            UnlockAchievement(AchievementType.FirstPickerAniCollected);

        }

        private void UnlockAchievement(AchievementType type)
        {
            var icon = AchievementIcons[(int)type];
            Debug.Log($"{type} Achievement Unlocked!");

            if (icon != null)
            {
                icon.enabled = true;
                StartCoroutine(DisplayIconCoroutine(icon));
            }
        }

        private IEnumerator DisplayIconCoroutine(Image icon)
        {
            while (icon.rectTransform.anchoredPosition.y < IconDestinationY)
            {
                Vector2 currentPos = icon.rectTransform.anchoredPosition;

                currentPos.y += _IconMoveSpeed * Time.deltaTime;
                
                icon.rectTransform.anchoredPosition = currentPos;
                yield return null;
            }
            
            yield return new WaitForSeconds(_IconDisplayDelaySeconds);
            icon.enabled = false;
        }
    }
}