using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using UnityEngine;

namespace AnimarsCatcher
{
    class TimeTask
    {
        public Action<int> callback;
        public double destinationTime;
        public double delay;
        public int count;

        public TimeTask(Action<int> callback, double destinationTime, double delay, int count)
        {
            this.callback = callback;
            this.delay = delay;
            this.destinationTime = destinationTime;
            this.count = count;
        }
    }

    public class Timer
    {
        private int _TaskID;
        private Dictionary<int, TimeTask> _TimeTaskMap = new Dictionary<int, TimeTask>();
        private Dictionary<int, TimeTask> _TempTimeTaskMap = new Dictionary<int, TimeTask>();

        public void Update()
        {
            foreach (var kvp in _TempTimeTaskMap)
            {
                _TimeTaskMap[kvp.Key] = kvp.Value; // key每次自增，不会重复
            }

            _TempTimeTaskMap.Clear();

            RemoveExpiredTasksFromTaskMap();
        }

        public int AddTask(Action<int> callback, double delay, int count = 1)
        {
            _TempTimeTaskMap.Add(++_TaskID, new TimeTask(callback, Time.time + delay, delay, count));
            return _TaskID;
        }

        public void DeleteTask(int deleteTaskID)
        {
            if (_TimeTaskMap.ContainsKey(deleteTaskID))
            {
                _TimeTaskMap.Remove(deleteTaskID);
            }

            if (_TempTimeTaskMap.ContainsKey(deleteTaskID))
            {
                _TempTimeTaskMap.Remove(deleteTaskID);
            }
        }

        private void RemoveExpiredTasksFromTaskMap()
        {
            var copy = new List<KeyValuePair<int, TimeTask>>(_TimeTaskMap);
            var keysToRemove = new List<int>();

            foreach (var pair in copy)
            {
                if (!_TimeTaskMap.ContainsKey(pair.Key)) continue;

                if (ProcessTask(pair.Value, pair.Key))
                {
                    keysToRemove.Add(pair.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _TimeTaskMap.Remove(key);
            }
        }

        private bool ProcessTask(TimeTask task, int taskKey)
        {
            bool isExpired = false;

            if (Time.time >= task.destinationTime)
            {
                task.callback?.Invoke(taskKey);

                if (task.count > 1)
                {
                    task.count -= 1;
                    task.destinationTime += task.delay;
                }
                else isExpired = true;
            }

            return isExpired;
        }
    }

}

