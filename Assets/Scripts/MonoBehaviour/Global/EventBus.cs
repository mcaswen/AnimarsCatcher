using UnityEngine;
using System.Collections.Generic;
using System;
using Object = System.Object;
using UnityEngine.Events;

namespace AnimarsCatcher.Presentation.Global
{
    /// <summary>
    /// 按事件数据类型隔离监听器的进程内事件总线
    /// </summary>
    public class EventBus : MonoBehaviour
    {
        public static EventBus Instance;
        // 使用 Object 完成类型擦除 实际值始终是与键匹配的 UnityEvent<T>
        private Dictionary<Type, Object> _eventMap = new Dictionary<Type, Object>();

        // 建立跨场景唯一实例
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

        // 按需创建强类型事件并恢复为调用方请求的泛型类型
        private UnityEvent<T> GetEvent<T>() where T : IEventData
        {
            var key = typeof(T);
            if (!_eventMap.TryGetValue(key, out var obj))
            {
                obj = new UnityEvent<T>();
                _eventMap[key] = obj;
            }
            return (UnityEvent<T>)obj;
        }

        /// <summary>
        /// 订阅指定事件数据类型
        /// </summary>
        public void Subscribe<T>(UnityAction<T> handler) where T : IEventData
            => GetEvent<T>().AddListener(handler);

        /// <summary>
        /// 取消指定事件数据类型的订阅
        /// </summary>
        public void Unsubscribe<T>(UnityAction<T> handler) where T : IEventData
            => GetEvent<T>().RemoveListener(handler);

        /// <summary>
        /// 向当前进程内的订阅者发布事件数据
        /// </summary>
        public void Publish<T>(T data) where T : IEventData
            => GetEvent<T>().Invoke(data);

    }

    /// <summary>
    /// 保留的非泛型事件总线实现
    /// 仅供现有引用兼容 新代码应使用 EventBus
    /// </summary>
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

            // 泛型委托无法转换为 IEventData 委托 此实现不会注册不同签名的处理器
            if (handler is UnityAction<IEventData> eventHandler)
                _eventMap[type].AddListener(eventHandler);
        }

        public void Unsubscribe<T>(UnityAction<IEventData> handler) where T : IEventData
        {
            var type = typeof(T);
            if (_eventMap.TryGetValue(type, out var existingEvent))
            {
                if (handler is UnityAction<IEventData> eventHandler)
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
