using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public enum FsmVarType : byte
{
    Int,
    Float,
    Bool,
    Float3,
    Entity
}

// 黑板缓冲区，封装对数据的高频读写
[InternalBufferCapacity(4)]
public struct FsmVar : IBufferElementData
{
    public uint Key;    // 数据标识符
    public FsmVarType Type;   // 类型标签
    
    public int Int;
    public float Float;
    public byte Bool;
    public Entity Entity;
    public float3 Float3;
}

[BurstCompile]
public static class Blackboard
{
    // 从动态缓冲区中获取数据
    [BurstCompile]
    public static bool TryGet(ref this DynamicBuffer<FsmVar> blackboard, uint key, out FsmVar v)
    {
        for (int i = 0; i < blackboard.Length; i++)
        {
            if (blackboard[i].Key == key)
            {
                v = blackboard[i];
                return true;
            }
        }

        v = default;
        return false;
    }

    // 获取方法族，类型不匹配时返回默认值
    public static int GetInt(ref this DynamicBuffer<FsmVar> blackboard, uint key, int def = 0)
    {
        return blackboard.TryGet(key, out var v) &&
            v.Type == FsmVarType.Int ? v.Int : def;
    }

    public static float GetFloat(ref this DynamicBuffer<FsmVar> blackboard, uint key, float def = 0)
    {
        return blackboard.TryGet(key, out var v) &&
            v.Type == FsmVarType.Float ? v.Float : def;
    }

    public static bool GetBool(ref this DynamicBuffer<FsmVar> blackboard, uint key, bool def = false)
    {
        return blackboard.TryGet(key, out var v) &&
            v.Type == FsmVarType.Bool ? v.Bool != 0 : def;
    }

    public static float3 GetFloat3(ref this DynamicBuffer<FsmVar> blackboard, uint key, float3 def = default)
    {
        return blackboard.TryGet(key, out var v) &&
            v.Type == FsmVarType.Float3 ? v.Float3 : def;
    }

    public static Entity GetEntity(ref this DynamicBuffer<FsmVar> blackboard, uint key, Entity def = default)
    {
        return blackboard.TryGet(key, out var v) &&
            v.Type == FsmVarType.Entity ? v.Entity : def;
    }

    // 写入方法族，存在则更新，不存在则添加
    [BurstCompile]
    public static void SetInt(ref this DynamicBuffer<FsmVar> blackboard, uint key, int value)
    {
        for (int i = 0; i < blackboard.Length; i++)
        {
            if (blackboard[i].Key == key)
            {
                var t = blackboard[i];
                t.Type = FsmVarType.Int;
                t.Int = value;
                blackboard[i] = t;
                return;
            }
        }

        blackboard.Add(new FsmVar
        {
            Key = key,
            Type = FsmVarType.Int,
            Int = value
        });
    }

    [BurstCompile]
    public static void SetFloat(ref this DynamicBuffer<FsmVar> blackboard, uint key, float value)
    {
        for (int i = 0; i < blackboard.Length; i++)
        {
            if (blackboard[i].Key == key)
            {
                var t = blackboard[i];
                t.Type = FsmVarType.Float;
                t.Float = value;
                blackboard[i] = t;
                return;
            }
        }

        blackboard.Add(new FsmVar
        {
            Key = key,
            Type = FsmVarType.Float,
            Float = value
        });
    }

    public static void SetFloat3(ref this DynamicBuffer<FsmVar> blackboard, uint key, float3 value)
    {
        for (int i = 0; i < blackboard.Length; i++)
        {
            if (blackboard[i].Key == key)
            {
                var t = blackboard[i];
                t.Type = FsmVarType.Float3;
                t.Float3 = value;
                blackboard[i] = t;
                return;
            }
        }

        blackboard.Add(new FsmVar
        {
            Key = key,
            Type = FsmVarType.Float3,
            Float3 = value
        });
    }

    [BurstCompile]
    public static void SetBool(ref this DynamicBuffer<FsmVar> blackboard, uint key, bool value)
    {
        for (int i = 0; i < blackboard.Length; i++)
        {
            if (blackboard[i].Key == key)
            {
                var t = blackboard[i];
                t.Type = FsmVarType.Bool;
                t.Bool = (byte)(value ? 1 : 0);
                blackboard[i] = t;
                return;
            }
        }

        blackboard.Add(new FsmVar
        {
            Key = key,
            Type = FsmVarType.Bool,
            Bool = (byte)(value ? 1 : 0)
        });
    }

    public static void SetEntity(ref this DynamicBuffer<FsmVar> blackboard, uint key, Entity value)
    {
        for (int i = 0; i < blackboard.Length; i++)
        {
            if (blackboard[i].Key == key)
            {
                var t = blackboard[i];
                t.Type = FsmVarType.Entity;
                t.Entity = value;
                blackboard[i] = t;
                return;
            }
        }

        blackboard.Add(new FsmVar
        {
            Key = key,
            Type = FsmVarType.Entity,
            Entity = value
        });
    }
}