using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using UnityEngine;
using Unity.VisualScripting;

namespace AnimarsCatcher.Mono.Utilities
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
        private int _taskID;
        private Dictionary<int, TimeTask> _timeTaskMap = new Dictionary<int, TimeTask>();
        private Dictionary<int, TimeTask> _tempTimeTaskMap = new Dictionary<int, TimeTask>();

        public void Update()
        {
            foreach (var kvp in _tempTimeTaskMap)
            {
                _timeTaskMap[kvp.Key] = kvp.Value; // key每次自增，不会重复
            }

            _tempTimeTaskMap.Clear();

            RemoveExpiredTasksFromTaskMap();
        }

        public int AddTask(Action<int> callback, double delay, int count = 1)
        {
            _tempTimeTaskMap.Add(++_taskID, new TimeTask(callback, Time.time + delay, delay, count));
            return _taskID;
        }

        public void DeleteTask(int deleteTaskID)
        {
            if (_timeTaskMap.ContainsKey(deleteTaskID)) // O(1)
            {
                _timeTaskMap.Remove(deleteTaskID); // O(1)
            }

            if (_tempTimeTaskMap.ContainsKey(deleteTaskID))
            {
                _tempTimeTaskMap.Remove(deleteTaskID);
            }
        }

        private void RemoveExpiredTasksFromTaskMap()
        {
            var copy = new List<KeyValuePair<int, TimeTask>>(_timeTaskMap);
            var keysToRemove = new List<int>();

            foreach (var pair in copy)
            {
                if (!_timeTaskMap.ContainsKey(pair.Key)) continue;

                if (ProcessTask(pair.Value, pair.Key))
                {
                    keysToRemove.Add(pair.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _timeTaskMap.Remove(key);
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
                else if (task.count == -1)
                {
                    task.destinationTime += task.delay;
                }
                else isExpired = true;
            }

            return isExpired;
        }
    }


    class TimeTask2
    {
        public int taskID;
        public Action<int> callback;
        public double destTime;
        public double delay;
        public int count;

        public TimeTask2(int taskID, Action<int> callback, double destTime, double delay, int count)
        {
            this.taskID = taskID;
            this.callback = callback;
            this.delay = delay;
            this.destTime = destTime;
            this.count = count;
        }
    }
    
    public class Timer2
    {
        private DateTime mStartDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);
        private double mNowTime;
        private int mTaskID;
        private List<int> mTaskIDList = new List<int>();
        private List<int> mRecycleTaskIDList = new List<int>();

        private List<TimeTask2> mTempTimeTaskList = new List<TimeTask2>();
        private List<TimeTask2> mTaskTimeList = new List<TimeTask2>();
        private List<int> mTempDeleteTimeTaskList = new List<int>();

        public Timer2()
        {
            Reset();
        }

        public void Update()
        {
            CheckTimeTask();
            DeleteTimeTask();

            if (mRecycleTaskIDList.Count > 0)
            {
                RecycleTaskID();
            }
        }

        public int AddTask(Action<int> callback, double delay, int count = 1)
        {
            int taskID = GetTaskID();
            mNowTime = GetUTCSeconds();
            mTempTimeTaskList.Add(new TimeTask2(taskID,callback,mNowTime+delay,delay,count));
            return taskID;
        }

        public void DeleteTask(int taskID)
        {
            mTempDeleteTimeTaskList.Add(taskID);
        }


        private void CheckTimeTask()
        {
            mTaskTimeList.AddRange(mTempTimeTaskList);
            mTempTimeTaskList.Clear();

            mNowTime = GetUTCSeconds();
            mTaskTimeList.RemoveAll(CheckSingleTask);
        }

        private bool CheckSingleTask(TimeTask2 task)
        {
            if (mNowTime >= task.destTime)
                {
                    task.callback?.Invoke(task.taskID);
                    if (task.count > 1)
                    {
                        task.count -= 1;
                        task.destTime += task.delay;
                        return false;
                    }
                    else
                    {
                        mRecycleTaskIDList.Add(task.taskID);
                        return true;
                    }
                }

                return false;
        }

        private void DeleteTimeTask()
        {
            foreach (var deleteTaskID in mTempDeleteTimeTaskList)
            {
                TimeTask2 task = mTaskTimeList.Find(t => t.taskID == deleteTaskID); // O(n)
                if (task != null)
                {
                    mTaskTimeList.Remove(task); // O(n)
                    mRecycleTaskIDList.Add(deleteTaskID);
                }
                
                task = mTempTimeTaskList.Find(t => t.taskID == deleteTaskID);
                if (task != null)
                {
                    mTempTimeTaskList.Remove(task);
                    mRecycleTaskIDList.Add(deleteTaskID);
                }
            }
            mTempDeleteTimeTaskList.Clear();
        }

        private void Reset()
        {
            mTaskID = 0;
            mTaskTimeList.Clear();
            mRecycleTaskIDList.Clear();
            mTempTimeTaskList.Clear();
            mTaskTimeList.Clear();
        }

        private int GetTaskID()
        {
            mTaskID += 1;
            while (mTaskIDList.Contains(mTaskID))
            {
                if (mTaskID == int.MaxValue) mTaskID = 0;
                mTaskID += 1;
            }
            mTaskIDList.Add(mTaskID);
            return mTaskID;
        }

        private void RecycleTaskID()
        {
            mRecycleTaskIDList.ForEach(taskID=>mTaskIDList.Remove(taskID));
            mRecycleTaskIDList.Clear();
        }

        private double GetUTCSeconds()
        {
            return (DateTime.UtcNow - mStartDateTime).TotalSeconds;
        }
    }


}

