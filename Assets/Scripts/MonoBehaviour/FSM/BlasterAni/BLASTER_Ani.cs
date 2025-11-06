using System;
using System.Collections;
using System.Collections.Generic;
using AnimarsCatcher.Mono.FSM;
using AnimarsCatcher.Mono.Items;
using UnityEngine;
using UnityEngine.AI;


namespace AnimarsCatcher.Mono
{
    public enum BlasterAniState
    {
        None = 0,
        Idle = 1,
        Follow = 2,
        Shoot = 3,
        Find = 4
    }

    public class BLASTER_Ani : MonoBehaviour
    {
        public Transform LeftHandIKTrans;
        public Transform RightHandIKTrans;
        
        private Animator _animator;
        private NavMeshAgent _agent;
        
        private StateMachine _stateMachine;
        
        public bool IsFollow = false;
        public bool IsShoot = false;
        public FragileItem FragileItem;
        private static readonly int Shoot1 = Animator.StringToHash("Shoot");


        public Transform GunTrans;

        public Vector3 Destination;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
        }

        private void Start()
        {
            _stateMachine = new StateMachine(new BlasterAni_Idle((int)BlasterAniState.Idle, this));
            
            BlasterAni_Follow followState = new BlasterAni_Follow((int) BlasterAniState.Follow, this);
            _stateMachine.AddState(followState);
            
            BlasterAni_Shoot shootState = new BlasterAni_Shoot((int) BlasterAniState.Shoot, this);
            _stateMachine.AddState(shootState);
        }

        private void Update()
        {
           _stateMachine.Update();
        }

        public void Shoot()
        {
            if (FragileItem != null) 
            {
                _animator.SetTrigger(Shoot1);

                Vector3 offset = FragileItem.transform.position - transform.position;
                Quaternion dir = Quaternion.LookRotation(offset);
                Instantiate(Resources.Load<GameObject>("FX_Beam"), GunTrans.position, dir);

                FragileItem.TakeDamage();
            }
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (_animator.GetCurrentAnimatorStateInfo(1).IsName("Shoot"))
            {
                _animator.SetIKPosition(AvatarIKGoal.LeftHand,LeftHandIKTrans.position);
                _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand,0.5f);
                _animator.SetIKRotation(AvatarIKGoal.LeftHand,LeftHandIKTrans.rotation);
                _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand,0.5f);
            
                _animator.SetIKPosition(AvatarIKGoal.RightHand,RightHandIKTrans.position);
                _animator.SetIKPositionWeight(AvatarIKGoal.RightHand,0.5f);
                _animator.SetIKRotation(AvatarIKGoal.RightHand,RightHandIKTrans.rotation);
                _animator.SetIKRotationWeight(AvatarIKGoal.RightHand,0.5f);
            }
        }
    }
}

