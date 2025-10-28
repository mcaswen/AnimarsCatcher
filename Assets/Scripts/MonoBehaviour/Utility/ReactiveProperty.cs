using System;
using UnityEngine;
using UnityEngine.Events;

namespace AnimarsCatcher
{
    public class ReactiveProperty<T> where T: IEquatable<T>
    {
        [SerializeField] private T _Value;
        private readonly UnityEvent<T> _OnValueChanged = new UnityEvent<T>();

        public T Value
        {
            get => _Value;
            set
            {
                if (!_Value.Equals(value))
                {
                    _Value = value;
                    _OnValueChanged.Invoke(_Value);
                }
            }
        }

        public ReactiveProperty()
        {
            _Value = default;
        }

        public ReactiveProperty(T initial)
        {
            Value = initial;
        }

        public void Subscribe(Action<T> callback)
        {
            if (callback == null)
            {
                Debug.LogError("[ReactiveProperty] Callback cannot be null!");
                return;
            }

            _OnValueChanged.AddListener(new UnityAction<T>(callback));
        }

        public void Unsubsribe(Action<T> callback)
        {
            if (callback == null)
            {
                Debug.LogError("[ReactiveProperty] Callback cannot be null!");
                return;
            }

            _OnValueChanged.RemoveListener(new UnityAction<T>(callback));
        }
    }
}
