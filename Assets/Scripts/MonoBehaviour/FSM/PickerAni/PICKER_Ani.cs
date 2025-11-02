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
        private StateMachine _StateMachine;

        [SerializeField] private float _Speed = 5f;
        public float Speed
        {
            get => _Speed;
            set
            {
                _Speed = value;
                _Animator.SetFloat(AniSpeed, _Speed);
                _NavmeshAgent.speed = _Speed;
            }
        }

        [SerializeField] private float _CarrySpeed = 3f;
        public float CarrySpeed
        {
            get => _CarrySpeed;
            set
            {
                _CarrySpeed = value;
                _Animator.SetFloat(AniSpeed, _CarrySpeed);
            }
        }

        private static readonly int AniSpeed = Animator.StringToHash("AniSpeed");
        
        public bool IsFollow = false;
        public bool IsPick = false;
        public bool ReadyToCarry = false;
        public PickableItem PickableItem;

        public Vector3 Destination;

        private Animator _Animator;
        private NavMeshAgent _NavmeshAgent;
        public Transform LeftHandEffector;
        public Transform RightHandEffector;

        private void Awake()
        {
            _Animator = GetComponent<Animator>();
            _NavmeshAgent = GetComponent<NavMeshAgent>();
        }

        private void Start()
        {
            _StateMachine = new StateMachine(new PickerAni_Idle((int)PickerAniState.Idle, this));
            
            PickerAni_Follow followState = new PickerAni_Follow((int)PickerAniState.Follow, this);
            _StateMachine.AddState(followState);
            
            PickerAni_Pick pickState = new PickerAni_Pick((int)PickerAniState.Pick, this);
            _StateMachine.AddState(pickState);
            
            PickerAni_Carry carryState = new PickerAni_Carry((int)PickerAniState.Carry, this);
            _StateMachine.AddState(carryState);

            // Generate Hand Effectors
            LeftHandEffector = new GameObject("LeftHandEffector").transform;
            LeftHandEffector.parent = transform;
            RightHandEffector = new GameObject("RightHandEffector").transform;
            RightHandEffector.parent = transform;
        }

        private void Update()
        {
            _StateMachine.Update();
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (_StateMachine.CurrentState.ID == (int)PickerAniState.Carry)
            {
                _Animator.SetIKPosition(AvatarIKGoal.LeftHand, LeftHandEffector.position);
                _Animator.SetIKPosition(AvatarIKGoal.RightHand, RightHandEffector.position);
                
                _Animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
                _Animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 1f);
                
                _Animator.SetIKRotation(AvatarIKGoal.LeftHand, LeftHandEffector.rotation);
                _Animator.SetIKRotation(AvatarIKGoal.RightHand, RightHandEffector.rotation);
                
                _Animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1f);
                _Animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 1f);
            }
        }
    }
}
