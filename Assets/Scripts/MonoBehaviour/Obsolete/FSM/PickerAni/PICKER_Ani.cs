using System;
using System.Collections;
using System.Collections.Generic;
using AnimarsCatcher.Mono.FSM;
using AnimarsCatcher.Mono.Items;
using UnityEngine;
using UnityEngine.AI;

namespace AnimarsCatcher.Mono
{
    public enum PickerAniState
    {
        None = 0,
        Idle = 1,
        Follow = 2,
        Pick = 3,
        Carry = 4
    }

    public class PICKER_Ani : MonoBehaviour
    {
        //State Machine
        private StateMachine _stateMachine;

        [SerializeField] private float _speed = 5f;
        public float Speed
        {
            get => _speed;
            set
            {
                _speed = value;
                _animator.SetFloat(AniSpeed, _speed);
                _navmeshAgent.speed = _speed;
            }
        }

        [SerializeField] private float _carrySpeed = 3f;
        public float CarrySpeed
        {
            get => _carrySpeed;
            set
            {
                _carrySpeed = value;
                _animator.SetFloat(AniSpeed, _carrySpeed);
            }
        }

        private static readonly int AniSpeed = Animator.StringToHash("AniSpeed");
        
        public bool IsFollow = false;
        public bool IsPick = false;
        public bool ReadyToCarry = false;
        public PickableItem PickableItem;

        public Vector3 Destination;

        private Animator _animator;
        private NavMeshAgent _navmeshAgent;
        public Transform LeftHandEffector;
        public Transform RightHandEffector;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _navmeshAgent = GetComponent<NavMeshAgent>();
        }

        private void Start()
        {
            _stateMachine = new StateMachine(new PickerAni_Idle((int)PickerAniState.Idle, this));
            
            PickerAni_Follow followState = new PickerAni_Follow((int)PickerAniState.Follow, this);
            _stateMachine.AddState(followState);
            
            PickerAni_Pick pickState = new PickerAni_Pick((int)PickerAniState.Pick, this);
            _stateMachine.AddState(pickState);
            
            PickerAni_Carry carryState = new PickerAni_Carry((int)PickerAniState.Carry, this);
            _stateMachine.AddState(carryState);

            // Generate Hand Effectors
            LeftHandEffector = new GameObject("LeftHandEffector").transform;
            LeftHandEffector.parent = transform;
            RightHandEffector = new GameObject("RightHandEffector").transform;
            RightHandEffector.parent = transform;
        }

        private void Update()
        {
            _stateMachine.Update();
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (_stateMachine.CurrentState.ID == (int)PickerAniState.Carry)
            {
                _animator.SetIKPosition(AvatarIKGoal.LeftHand, LeftHandEffector.position);
                _animator.SetIKPosition(AvatarIKGoal.RightHand, RightHandEffector.position);
                
                _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
                _animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 1f);
                
                _animator.SetIKRotation(AvatarIKGoal.LeftHand, LeftHandEffector.rotation);
                _animator.SetIKRotation(AvatarIKGoal.RightHand, RightHandEffector.rotation);
                
                _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1f);
                _animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 1f);
            }
        }
    }
}
