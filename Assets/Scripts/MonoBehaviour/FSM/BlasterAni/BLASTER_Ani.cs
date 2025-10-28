using System;
using System.Collections;
using System.Collections.Generic;
using AnimarsCatcher.FSM;
using UnityEngine;
using UnityEngine.AI;

namespace AnimarsCatcher
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
        
        private Animator _Animator;
        private NavMeshAgent _Agent;
        
        private StateMachine _StateMachine;
        
        public bool IsFollow = false;
        public bool IsShoot = false;
        public FragileItem FragileItem;
        private static readonly int Shoot1 = Animator.StringToHash("Shoot");


        public Transform GunTrans;

        public Vector3 Destination;

        private void Awake()
        {
            _Animator = GetComponent<Animator>();
        }

        private void Start()
        {
            _StateMachine = new StateMachine(new BlasterAni_Idle((int)BlasterAniState.Idle, this));
            
            BlasterAni_Follow followState = new BlasterAni_Follow((int) BlasterAniState.Follow, this);
            _StateMachine.AddState(followState);
            
            BlasterAni_Shoot shootState = new BlasterAni_Shoot((int) BlasterAniState.Shoot, this);
            _StateMachine.AddState(shootState);
        }

        private void Update()
        {
           _StateMachine.Update();
        }

        public void Shoot()
        {
            if (FragileItem != null) 
            {
                _Animator.SetTrigger(Shoot1);

                Vector3 offset = FragileItem.transform.position - transform.position;
                Quaternion dir = Quaternion.LookRotation(offset);
                Instantiate(Resources.Load<GameObject>("FX_Beam"), GunTrans.position, dir);

                FragileItem.TakeDamage();
            }
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (_Animator.GetCurrentAnimatorStateInfo(1).IsName("Shoot"))
            {
                _Animator.SetIKPosition(AvatarIKGoal.LeftHand,LeftHandIKTrans.position);
                _Animator.SetIKPositionWeight(AvatarIKGoal.LeftHand,0.5f);
                _Animator.SetIKRotation(AvatarIKGoal.LeftHand,LeftHandIKTrans.rotation);
                _Animator.SetIKRotationWeight(AvatarIKGoal.LeftHand,0.5f);
            
                _Animator.SetIKPosition(AvatarIKGoal.RightHand,RightHandIKTrans.position);
                _Animator.SetIKPositionWeight(AvatarIKGoal.RightHand,0.5f);
                _Animator.SetIKRotation(AvatarIKGoal.RightHand,RightHandIKTrans.rotation);
                _Animator.SetIKRotationWeight(AvatarIKGoal.RightHand,0.5f);
            }
        }
    }
}

