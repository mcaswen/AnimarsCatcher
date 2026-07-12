using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>记录只在指定网络 Tick 生效一次的输入脉冲</summary>
public struct FixedInputEvent
{
    private byte _wasEverSet;
    private uint _lastSetTick;

    /// <summary>将输入脉冲绑定到指定网络 Tick</summary>
    /// <param name="tick">脉冲生效 Tick</param>
    public void Set(uint tick)
    {
        _lastSetTick = tick;
        _wasEverSet = 1;
    }

    /// <summary>判断输入脉冲是否在指定 Tick 生效</summary>
    /// <param name="tick">待检查 Tick</param>
    /// <returns>脉冲是否在该 Tick 生效</returns>
    public bool IsSet(uint tick)
    {
        if (_wasEverSet == 1)
        {
            return tick == _lastSetTick;
        }

        return false;
    }
}
