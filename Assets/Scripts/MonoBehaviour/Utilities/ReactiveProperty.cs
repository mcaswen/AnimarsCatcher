using System;
using UnityEngine;
using UnityEngine.Events;

namespace AnimarsCatcher.Mono.Utilities
{
    public class ReactiveProperty<T> where T : IEquatable<T>
    {
        [SerializeField] private T _value;
        private readonly UnityEvent<T> _onValueChanged = new UnityEvent<T>();

        public T Value
        {
            get => _value;
            set
            {
                if (!_value.Equals(value))
                {
                    _value = value;
                    _onValueChanged.Invoke(_value);
                }
            }
        }

        public ReactiveProperty()
        {
            _value = default;
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

            _onValueChanged.AddListener(new UnityAction<T>(callback));
        }

        public void Unsubscribe(Action<T> callback)
        {
            if (callback == null)
            {
                Debug.LogError("[ReactiveProperty] Callback cannot be null!");
                return;
            }

            _onValueChanged.RemoveListener(new UnityAction<T>(callback));
        }
    }

    public class ReactiveProperty2<T> where T : IEquatable<T>
    {
        private T mValue;
        private Action<T> mOnValueChanged;

        public T Value
        {
            get => mValue;
            set
            {
                if (!mValue.Equals(value))
                {
                    mValue = value;
                    mOnValueChanged?.Invoke(mValue);
                }
            }
        }

        public ReactiveProperty2(T initialValue, Action<T> onValueChanged = null)
        {
            mValue = initialValue;
            mOnValueChanged = onValueChanged;
        }

        public ReactiveProperty2(Action<T> onValueChanged = null)
        {
            mOnValueChanged = onValueChanged;
        }

        public void Subscribe(Action<T> callback)
        {
            mOnValueChanged += callback;
        }

        public void Unsubscribe(Action<T> callback)
        {
            mOnValueChanged -= callback;
        }
    }
}

