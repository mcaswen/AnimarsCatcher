using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AnimarsCatcher.Mono
{
    public class MinimalCamera : MonoBehaviour
    {
        [SerializeField] private Transform _PlayerTrans;
        private Vector3 _Offset;
        
        void Update()
        {
            if (_PlayerTrans == null)
            {
                var playerObj = GameObject.FindWithTag("Player");
                if (playerObj != null)
                {
                    _PlayerTrans = playerObj.transform;
                    _Offset = _PlayerTrans.position - transform.position;
                }
            }
        }

        private void LateUpdate()
        {
            if (_PlayerTrans != null)
                transform.position = _PlayerTrans.position - _Offset;
        }
    }
}


