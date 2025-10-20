using UnityEngine;

namespace AnimarsCatcher
{
    public class PickerAni_Carry : PickerAniStateBase
    {
        private Vector3 _TargetPosition;
        public PickerAni_Carry(int id, PICKER_Ani o) : base(id, o)
        {
        }

        public override void OnEnter(params object[] args)
        {
            _NavmeshAgent.isStopped = true;
            _NavmeshAgent.enabled = false;
        }

        public override void OnStay(params object[] args)
        {
            if (Owner.IsPick && Owner.ReadyToCarry)
            {
                _TargetPosition = Owner.PickableItem.GetPosition(Owner);

                Owner.transform.position = _TargetPosition;
                Owner.transform.forward = Owner.PickableItem.transform.forward;

                mAnimator.SetFloat(AniSpeed, Owner.CarrySpeed);

                Owner.LeftHandEffector.position = _TargetPosition;
                Owner.RightHandEffector.position = _TargetPosition;
            }
            else if (!Owner.IsPick && !Owner.ReadyToCarry)
            {
                mAnimator.SetFloat(AniSpeed, 0f);
                StateMachine.TranslateState((int)PickerAniState.Follow);
            }
        }

        public override void OnExit(params object[] args)
        {
            _NavmeshAgent.enabled = true;
        }
    }
}