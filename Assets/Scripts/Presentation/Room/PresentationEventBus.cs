using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Scripting.APIUpdating;
using Object = System.Object;

namespace AnimarsCatcher.Presentation.Room
{
    /// <summary>
    /// 按事件类型隔离房间界面监听器的进程内事件总线
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.Global", "AnimarsCatcher.Presentation", "EventBus")]
    public class PresentationEventBus : MonoBehaviour
    {
        public static PresentationEventBus Instance { get; private set; }

        private readonly Dictionary<Type, Object> _eventMap = new();

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                return;
            }

            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private UnityEvent<T> GetEvent<T>() where T : IPresentationEvent
        {
            Type eventType = typeof(T);
            if (!_eventMap.TryGetValue(eventType, out Object eventObject))
            {
                eventObject = new UnityEvent<T>();
                _eventMap[eventType] = eventObject;
            }

            return (UnityEvent<T>)eventObject;
        }

        /// <summary>
        /// 订阅指定表现事件
        /// </summary>
        public void Subscribe<T>(UnityAction<T> handler) where T : IPresentationEvent
            => GetEvent<T>().AddListener(handler);

        /// <summary>
        /// 取消订阅指定表现事件
        /// </summary>
        public void Unsubscribe<T>(UnityAction<T> handler) where T : IPresentationEvent
            => GetEvent<T>().RemoveListener(handler);

        /// <summary>
        /// 发布指定表现事件
        /// </summary>
        public void Publish<T>(T eventData) where T : IPresentationEvent
            => GetEvent<T>().Invoke(eventData);
    }
}
