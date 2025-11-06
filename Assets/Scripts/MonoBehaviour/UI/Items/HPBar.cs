using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Utilities;

namespace AnimarsCatcher.Mono.UI
{
    public class HPBar : MonoBehaviour
    {
        private ReactiveProperty<int> _hP;
        private Image _hPBar;
        private int _hPMax;

        private void Awake()
        {
            _hPBar = GetComponent<Image>();
        }

        public void Init(ReactiveProperty<int> hp)
        {
            _hP = hp;
            _hP.Subscribe(OnHPChanged);
            _hPMax = _hP.Value;
        }

        private void OnHPChanged(int hp)
        {
            _hPBar.fillAmount = (float)hp / _hPMax;
            if (hp <= 0)
            {
                Destroy(transform.parent.parent.gameObject);
            }
        }

        private void OnDestroy()
        {
            _hP.Unsubscribe(OnHPChanged);
        }

    }
}

