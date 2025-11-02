using UnityEngine;
using System.Collections.Generic;
using System;
using Object = System.Object;
using UnityEngine.Events;

namespace AnimarsCatcher.Mono.Global
{

    public class EventBus : MonoBehaviour
    {
        public static EventBus Instance;
        private Dictionary<Type, Object> _eventMap = new Dictionary<Type, Object>(); // 类型擦除，T (type of IEventData)类型 作为键 与 UnityEvent<IEventData> 事件 作为值 的映射

        void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private UnityEvent<T> GetEvent<T>() where T : IEventData // 通过 Object 类型存储不同类型的 UnityEvent<T> 实例
        {
            var key = typeof(T);
            if (!_eventMap.TryGetValue(key, out var obj))
            {
                obj = new UnityEvent<T>(); // 创建新的事件实例
                _eventMap[key] = obj;
            }
            return (UnityEvent<T>)obj; // 强制类型转换（必定成功，因为obj实例化时已是 UnityEvent<T> 类型）
        }

        // 若调用这些方法时 订阅类型与委托处理类型不匹配，则会在编译期抛出异常，防止运行时错误
        public void Subscribe<T>(UnityAction<T> handler) where T : IEventData
            => GetEvent<T>().AddListener(handler);

        public void Unsubscribe<T>(UnityAction<T> handler) where T : IEventData
            => GetEvent<T>().RemoveListener(handler);

        public void Publish<T>(T data) where T : IEventData
            => GetEvent<T>().Invoke(data);

    }

    public class EventBus2 : MonoBehaviour
    {
        public static EventBus2 Instance;
        private Dictionary<Type, UnityEvent<IEventData>> _eventMap = new Dictionary<Type, UnityEvent<IEventData>>();

        void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        public void Subscribe<T>(UnityAction<T> handler) where T : IEventData
        {
            var type = typeof(T);

            if (!_eventMap.TryGetValue(type, out var existingEvent))
            {
                _eventMap[type] = new UnityEvent<IEventData>();
            }

            if (handler is UnityAction<IEventData> eventHandler) // 永远为 False
                _eventMap[type].AddListener(eventHandler);
        }

        public void Unsubscribe<T>(UnityAction<IEventData> handler) where T : IEventData
        {
            var type = typeof(T);
            if (_eventMap.TryGetValue(type, out var existingEvent))
            {
                if (handler is UnityAction<IEventData> eventHandler) // 永远为 False
                    _eventMap[type].RemoveListener(eventHandler);
            }
        }

        public void TriggerEvent<T>(T eventData) where T : IEventData
        {
            var type = typeof(T);
            if (_eventMap.TryGetValue(type, out var existingEvent))
            {
                existingEvent.Invoke(eventData);
            }
        }

    }
}