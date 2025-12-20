using UnityEngine;

namespace AnimarsCatcher.Mono
{
    public class PickerAni_Pick:PickerAniStateBase
    {
        private Transform mPickableItemTrans;
        public PickerAni_Pick(int id, PICKER_Ani o) : base(id, o)
        {
        }

        public override void OnEnter(params object[] args)
        {
            mPickableItemTrans = Owner.PickableItem.transform;
        }

        public override void OnStay(params object[] args)
        {
            if (FindPickableItem() && !Owner.ReadyToCarry)
            {
                Owner.PickableItem.AddPickerAni(Owner);
                Owner.ReadyToCarry = true;
            }

            if (Owner.PickableItem.CheckCanCarry())
            {
                StateMachine.TranslateState((int)PickerAniState.Carry);
            }
        }

        private bool FindPickableItem()
        {
            if (Vector3.Distance(Owner.transform.position, mPickableItemTrans.position)
                <= _NavmeshAgent.stoppingDistance)
            {
                _NavmeshAgent.isStopped = true;
                mAnimator.SetFloat(AniSpeed, 0);
                return true;
            }
            else
            {
                _NavmeshAgent.isStopped = false;
                mAnimator.SetFloat(AniSpeed, Owner.Speed);
                _NavmeshAgent.SetDestination(mPickableItemTrans.position);
                return false;
            }
        }
    }
}