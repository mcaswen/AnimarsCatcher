using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Pool;
using AnimarsCatcher.Mono.Items;

namespace AnimarsCatcher.Mono
{
    public class FX_Beam : MonoBehaviour
    {
        public IObjectPool<GameObject> BeamPool;
        private GameObject mHit;

        private void OnEnable()
        {
            mHit = gameObject;
        }

        private IEnumerator OnParticleCollision(GameObject other)
        {
            if (!mHit.Equals(other))
            {
                mHit = other;
                if (other.CompareTag("FragileItem"))
                {
                    other.GetComponent<FragileItem>().Health.Value -= 10;
                }
            }

            yield return new WaitForSeconds(2f);
            BeamPool.Release(gameObject);
        }
    }
}