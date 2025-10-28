using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;

namespace AnimarsCatcher
{
    public interface ICanPick
    {
        bool CheckCanPick();
        bool CheckCanCarry();
    }

    public enum PickableItemType
    {
        Food,
        Crystal
    }

    public class PickableItem : MonoBehaviour, ICanPick, IResource
    {
        
        [SerializeField] private int _ResourceCount = 1;
        public int ResourceCount => _ResourceCount;
        public PickableItemType ItemType;

        [SerializeField] private bool _IsSpecialItem = false;
        [SerializeField] private float _AniSpeedMutiplier = 1.5f;
        [SerializeField] private string _PickerAniTag = "PICKER_Ani";

        public List<Vector3> Positions = new List<Vector3>();

        public int MaxAniCount = 2;
        public int CurrentAniCount = 0;
        private List<PICKER_Ani> _Anis;
        private NavMeshAgent _TeamAgent;
        private Transform _HomeTransform;

        private LayerMask _LayerMask;

        private void Awake()
        {
            _Anis = new List<PICKER_Ani>();
            _TeamAgent = GetComponent<NavMeshAgent>();
            _TeamAgent.enabled = false;
            _HomeTransform = GameObject.FindWithTag("Home").transform;

            _LayerMask = gameObject.layer;
        }

        private void Update()
        {
            TeamAgentMove();
        }

        private void TeamAgentMove()
        {
            if (CheckCanCarry() && !_TeamAgent.enabled)
            {
                _TeamAgent.enabled = true;
            }

            if (_TeamAgent.enabled)
            {
                _TeamAgent.SetDestination(_HomeTransform.position);
                _TeamAgent.speed = _Anis[0].CarrySpeed;
            }

            if (Vector3.Distance(transform.GetPositionOnTerrain(), _HomeTransform.position)
                < _TeamAgent.stoppingDistance)
            {
                foreach (var ani in _Anis)
                {
                    ani.ReadyToCarry = false;
                    ani.IsPick = false;
                }

                _Anis.Clear();
                Positions.Clear();

                switch (ItemType)
                {
                    case PickableItemType.Food:
                        FindObjectOfType<GameRoot>().GameModel.FoodSum.Value += _ResourceCount;
                        break;
                    case PickableItemType.Crystal:
                        FindObjectOfType<GameRoot>().GameModel.CrystalSum.Value += _ResourceCount;
                        break;
                    default:
                        break;
                }
                if (_IsSpecialItem)
                {
                    AccelerateAllAnis();
                    AchievementManager.Instance.RecordSpecialItemCollected();
                }

                Destroy(gameObject);
            }
        }

        public Vector3 GetPosition(PICKER_Ani ani)
        {
            return transform.TransformPoint(Positions[_Anis.IndexOf(ani)]);
        }

        public bool CheckCanPick()
        {
            return CurrentAniCount < MaxAniCount;
        }

        public bool CheckCanCarry()
        {
            if (CurrentAniCount > 0 && CurrentAniCount >= MaxAniCount / 2)
            {
                foreach (var ani in _Anis)
                {
                    if (!ani.ReadyToCarry) return false;
                }
                return true;
            }
            return false;
        }

        public void AddPickerAni(PICKER_Ani pickerAni)
        {
            _Anis.Add(pickerAni);
            CurrentAniCount++;
        }

        public void AccelerateAllAnis()
        {
            GameObject[] AniObjects = GameObject.FindGameObjectsWithTag(_PickerAniTag);

            foreach (var ani in AniObjects)
            {
                var pickerAni = ani.GetComponent<PICKER_Ani>();
                
                pickerAni.Speed *= _AniSpeedMutiplier;
                pickerAni.CarrySpeed *= _AniSpeedMutiplier;
            }

        }

        private void OnMouseEnter()
        {
            gameObject.layer = LayerMask.NameToLayer("SelectedObject");
        }

        private void OnMouseExit()
        {
            gameObject.layer = _LayerMask;
        }
    }
}