using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace AnimarsCatcher.Core.Fsm
{
    /// <summary>
    /// 标识黑板条目当前保存的数据类型
    /// </summary>
    public enum FsmVarType : byte
    {
        Int,
        Float,
        Bool,
        Float3,
        Entity
    }

    /// <summary>
    /// 使用显式类型标签在动态缓冲区中保存可同步的状态机变量
    /// </summary>
    [InternalBufferCapacity(4)]
    [GhostComponent]
    public struct FsmVar : IBufferElementData
    {
        [GhostField]
        public uint Key;    // 数据标识符

        [GhostField]
        public FsmVarType Type;   // 类型标签

        [GhostField]
        public int Int;

        [GhostField]
        public float Float;

        [GhostField]
        public byte Bool;

        [GhostField]
        public Entity Entity;

        [GhostField]
        public float3 Float3;
    }

    /// <summary>
    /// 提供按键访问黑板缓冲区的类型安全读写方法
    /// </summary>
    [BurstCompile]
    public static class Blackboard
    {
        /// <summary>
        /// 按键在线性缓冲区中查找原始黑板条目
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">业务定义的变量键</param>
        /// <param name="value">成功时返回原始条目</param>
        /// <returns>找到对应键时返回真</returns>
        [BurstCompile]
        public static bool TryGet(ref this DynamicBuffer<FsmVar> blackboard, uint key, out FsmVar value)
        {
            for (int i = 0; i < blackboard.Length; i++)
            {
                if (blackboard[i].Key == key)
                {
                    value = blackboard[i];
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// 读取整数值，键不存在或类型不匹配时返回默认值
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">整数变量键</param>
        /// <param name="defaultValue">读取失败时的默认值</param>
        /// <returns>黑板中的整数或默认值</returns>
        public static int GetInt(ref this DynamicBuffer<FsmVar> blackboard, uint key, int defaultValue = 0)
        {
            return blackboard.TryGet(key, out var value) &&
                value.Type == FsmVarType.Int ? value.Int : defaultValue;
        }

        /// <summary>
        /// 读取浮点值，键不存在或类型不匹配时返回默认值
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">浮点变量键</param>
        /// <param name="defaultValue">读取失败时的默认值</param>
        /// <returns>黑板中的浮点值或默认值</returns>
        public static float GetFloat(ref this DynamicBuffer<FsmVar> blackboard, uint key, float defaultValue = 0)
        {
            return blackboard.TryGet(key, out var value) &&
                value.Type == FsmVarType.Float ? value.Float : defaultValue;
        }

        /// <summary>
        /// 读取布尔值，键不存在或类型不匹配时返回默认值
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">布尔变量键</param>
        /// <param name="defaultValue">读取失败时的默认值</param>
        /// <returns>黑板中的布尔值或默认值</returns>
        public static bool GetBool(ref this DynamicBuffer<FsmVar> blackboard, uint key, bool defaultValue = false)
        {
            return blackboard.TryGet(key, out var value) &&
                value.Type == FsmVarType.Bool ? value.Bool != 0 : defaultValue;
        }

        /// <summary>
        /// 读取三维向量，键不存在或类型不匹配时返回默认值
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">三维向量变量键</param>
        /// <param name="defaultValue">读取失败时的默认值</param>
        /// <returns>黑板中的三维向量或默认值</returns>
        public static float3 GetFloat3(ref this DynamicBuffer<FsmVar> blackboard, uint key, float3 defaultValue = default)
        {
            return blackboard.TryGet(key, out var value) &&
                value.Type == FsmVarType.Float3 ? value.Float3 : defaultValue;
        }

        /// <summary>
        /// 读取实体引用，键不存在或类型不匹配时返回默认值
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">实体变量键</param>
        /// <param name="defaultValue">读取失败时的默认值</param>
        /// <returns>黑板中的实体引用或默认值</returns>
        public static Entity GetEntity(ref this DynamicBuffer<FsmVar> blackboard, uint key, Entity defaultValue = default)
        {
            return blackboard.TryGet(key, out var value) &&
                value.Type == FsmVarType.Entity ? value.Entity : defaultValue;
        }

        /// <summary>
        /// 写入整数值，键存在时原位更新，否则追加新条目
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">整数变量键</param>
        /// <param name="value">需要保存的整数</param>
        [BurstCompile]
        public static void SetInt(ref this DynamicBuffer<FsmVar> blackboard, uint key, int value)
        {
            for (int i = 0; i < blackboard.Length; i++)
            {
                if (blackboard[i].Key == key)
                {
                    var entry = blackboard[i];
                    entry.Type = FsmVarType.Int;
                    entry.Int = value;
                    blackboard[i] = entry;
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

        /// <summary>
        /// 写入浮点值，键存在时原位更新，否则追加新条目
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">浮点变量键</param>
        /// <param name="value">需要保存的浮点值</param>
        [BurstCompile]
        public static void SetFloat(ref this DynamicBuffer<FsmVar> blackboard, uint key, float value)
        {
            for (int i = 0; i < blackboard.Length; i++)
            {
                if (blackboard[i].Key == key)
                {
                    var entry = blackboard[i];
                    entry.Type = FsmVarType.Float;
                    entry.Float = value;
                    blackboard[i] = entry;
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

        /// <summary>
        /// 写入三维向量，键存在时原位更新，否则追加新条目
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">三维向量变量键</param>
        /// <param name="value">需要保存的三维向量</param>
        public static void SetFloat3(ref this DynamicBuffer<FsmVar> blackboard, uint key, float3 value)
        {
            for (int i = 0; i < blackboard.Length; i++)
            {
                if (blackboard[i].Key == key)
                {
                    var entry = blackboard[i];
                    entry.Type = FsmVarType.Float3;
                    entry.Float3 = value;
                    blackboard[i] = entry;
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

        /// <summary>
        /// 写入布尔值，键存在时原位更新，否则追加新条目
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">布尔变量键</param>
        /// <param name="value">需要保存的布尔值</param>
        [BurstCompile]
        public static void SetBool(ref this DynamicBuffer<FsmVar> blackboard, uint key, bool value)
        {
            for (int i = 0; i < blackboard.Length; i++)
            {
                if (blackboard[i].Key == key)
                {
                    var entry = blackboard[i];
                    entry.Type = FsmVarType.Bool;
                    entry.Bool = (byte)(value ? 1 : 0);
                    blackboard[i] = entry;
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

        /// <summary>
        /// 写入实体引用，键存在时原位更新，否则追加新条目
        /// </summary>
        /// <param name="blackboard">实体黑板缓冲区</param>
        /// <param name="key">实体变量键</param>
        /// <param name="value">需要保存的实体引用</param>
        public static void SetEntity(ref this DynamicBuffer<FsmVar> blackboard, uint key, Entity value)
        {
            for (int i = 0; i < blackboard.Length; i++)
            {
                if (blackboard[i].Key == key)
                {
                    var entry = blackboard[i];
                    entry.Type = FsmVarType.Entity;
                    entry.Entity = value;
                    blackboard[i] = entry;
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
}
