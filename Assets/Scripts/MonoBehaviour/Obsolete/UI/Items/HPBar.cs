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
        private ReactiveProperty<int> _hp;
        private Image _hpBar;
        private int _hpMax;

        private void Awake()
        {
            _hpBar = GetComponent<Image>();
        }

        public void Initialize(ReactiveProperty<int> hp)
        {
            _hp = hp;
            _hp.Subscribe(OnHPChanged);
            _hpMax = _hp.Value;
        }

        private void OnHPChanged(int hp)
        {
            _hpBar.fillAmount = (float)hp / _hpMax;
            if (hp <= 0)
            {
                Destroy(transform.parent.parent.gameObject);
            }
        }

        private void OnDestroy()
        {
            _hp.Unsubscribe(OnHPChanged);
        }

    }
}

