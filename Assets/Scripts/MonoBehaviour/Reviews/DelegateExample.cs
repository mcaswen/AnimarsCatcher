using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;

public delegate void MyAction();

class Clock
{
    public event MyAction OnSevenAM;
    private int _currentTime;

    public void UpdatePerHour()
    {
        if (_currentTime >= 7) return;

        Debug.Log("滴答");
        _currentTime++;

        if (_currentTime == 7)
        {
            RingAtSeven();
        }
    }

    private void RingAtSeven()
    {
        Debug.Log("丁铃铃 7点了！");
        OnSevenAM?.Invoke();
    }

}

class CollegeStudentWithEarlyEight
{

    public void GetUp()
    {
        Debug.Log("好困但还是要起床");
    }

    public void SleepAtSevenAM()
    {
        Debug.Log("再睡一会，不会迟到的");
    }

}

class DelegateExample : MonoBehaviour
{
    private Clock _clock;
    private CollegeStudentWithEarlyEight _student;

    void Awake()
    {
        _student = new CollegeStudentWithEarlyEight();
        _clock = new Clock();
        _clock.OnSevenAM += _student.SleepAtSevenAM;
        _clock.OnSevenAM += _student.GetUp;
        _clock.OnSevenAM = _student.GetUp;
        _clock.OnSevenAM.Invoke();
    }

    void Update()
    {
        _clock.UpdatePerHour();
    }
}