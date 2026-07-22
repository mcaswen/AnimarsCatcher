using System;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using System.Runtime.InteropServices;
using Unity.Assertions;

namespace Unity.NetCode
{
    /// <summary>
    /// 存储 NetCode 序列化 Job 中需要读取和写入的全部 Component 类型
    /// 此结构体用于规避 IJob 的限制，即 <see cref="DynamicComponentTypeHandle"/> 必须定义为字段，
    /// 包含 <see cref="DynamicComponentTypeHandle"/> 的集合无效
    /// </summary>
    /// <remarks>
    /// 使用此结构体的 256 项版本时，如果将其作为参数传递，包括传给作为扩展方法的 Job Schedule，
    /// 会触发 "InvalidProgramException: Passing an argument of size ..."
    /// 这是 Mono 的限制，应改用 ScheduleByRef
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DynamicTypeList
    {
        #if NETCODE_COMPONENTS_256
        public const int MaxCapacity = 256;
        #else
        public const int MaxCapacity = 128;
        #endif

        public static unsafe void PopulateList(ref SystemState system, DynamicBuffer<GhostCollectionComponentType> ghostComponentCollection, bool readOnly, ref DynamicTypeList list)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<DynamicComponentTypeHandle32>() != UnsafeUtility.SizeOf<DynamicComponentTypeHandle>()*32)
                throw new System.Exception("Invalid type size, this will cause undefined behavior");
#endif
            var listLength = ghostComponentCollection.Length;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (listLength > MaxCapacity)
                throw new System.Exception($"Invalid number of components used for ghost serialization: {listLength}, max is {MaxCapacity}. The maximum limit can be increased up to 256 by defining NETCODE_COMPONENTS_256.");
#endif
            DynamicComponentTypeHandle* GhostChunkComponentTypesPtr = list.GetData();
            list.Length = listLength;
            for (int i = 0; i < list.Length; ++i)
            {
                var compType = ghostComponentCollection[i].Type;
                if (readOnly)
                    compType.AccessModeType = ComponentType.AccessMode.ReadOnly;
                GhostChunkComponentTypesPtr[i] = system.GetDynamicComponentTypeHandle(compType);
            }
        }

        public static unsafe void PopulateListFromArray(ref SystemState system, NativeArray<ComponentType> componentTypes,  bool readOnly, ref DynamicTypeList list)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<DynamicComponentTypeHandle32>() != UnsafeUtility.SizeOf<DynamicComponentTypeHandle>()*32)
                throw new System.Exception("Invalid type size, this will cause undefined behavior");
#endif

            DynamicComponentTypeHandle* componentTypesPtr = list.GetData();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (componentTypes.Length > MaxCapacity)
                throw new System.Exception($"Invalid number of components used for ghost serialization: {componentTypes.Length}, max is {MaxCapacity}. The maximum limit can be increased up to 256 by defining NETCODE_COMPONENTS_256.");
#endif
            list.Length = componentTypes.Length;
            for (int i = 0; i < list.Length; ++i)
            {
                var compType = componentTypes[i];
                if (readOnly)
                    compType.AccessModeType = ComponentType.AccessMode.ReadOnly;
                componentTypesPtr[i] = system.GetDynamicComponentTypeHandle(compType);
            }
        }

        private DynamicComponentTypeHandle32 dynamicType000;
#pragma warning disable 0169
        private DynamicComponentTypeHandle32 dynamicType032;
        private DynamicComponentTypeHandle32 dynamicType064;
        private DynamicComponentTypeHandle32 dynamicType096;
        #if NETCODE_COMPONENTS_256
        private DynamicComponentTypeHandle32 dynamicType128;
        private DynamicComponentTypeHandle32 dynamicType160;
        private DynamicComponentTypeHandle32 dynamicType192;
        private DynamicComponentTypeHandle32 dynamicType224;
        #endif
#pragma warning restore 0169
        public int Length { get; set; }

        public unsafe DynamicComponentTypeHandle* GetData()
        {
            fixed (DynamicComponentTypeHandle* ptr = &dynamicType000.dynamicType00)
            {
                return ptr;
            }
        }

        public unsafe Span<DynamicComponentTypeHandle> AsSpan()
        {
            fixed (DynamicComponentTypeHandle* ptr = &dynamicType000.dynamicType00)
            {
                return new Span<DynamicComponentTypeHandle>(ptr, Length);
            }
        }
    }

    /// <summary>
    /// 此结构体用于规避 IJob 的限制，即 <see cref="DynamicComponentTypeHandle"/> 必须定义为字段，
    /// 包含 <see cref="DynamicComponentTypeHandle"/> 的集合无效
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DynamicComponentTypeHandle32
    {
        public DynamicComponentTypeHandle dynamicType00;
        public DynamicComponentTypeHandle dynamicType01;
        public DynamicComponentTypeHandle dynamicType02;
        public DynamicComponentTypeHandle dynamicType03;
        public DynamicComponentTypeHandle dynamicType04;
        public DynamicComponentTypeHandle dynamicType05;
        public DynamicComponentTypeHandle dynamicType06;
        public DynamicComponentTypeHandle dynamicType07;
        public DynamicComponentTypeHandle dynamicType08;
        public DynamicComponentTypeHandle dynamicType09;
        public DynamicComponentTypeHandle dynamicType10;
        public DynamicComponentTypeHandle dynamicType11;
        public DynamicComponentTypeHandle dynamicType12;
        public DynamicComponentTypeHandle dynamicType13;
        public DynamicComponentTypeHandle dynamicType14;
        public DynamicComponentTypeHandle dynamicType15;
        public DynamicComponentTypeHandle dynamicType16;
        public DynamicComponentTypeHandle dynamicType17;
        public DynamicComponentTypeHandle dynamicType18;
        public DynamicComponentTypeHandle dynamicType19;
        public DynamicComponentTypeHandle dynamicType20;
        public DynamicComponentTypeHandle dynamicType21;
        public DynamicComponentTypeHandle dynamicType22;
        public DynamicComponentTypeHandle dynamicType23;
        public DynamicComponentTypeHandle dynamicType24;
        public DynamicComponentTypeHandle dynamicType25;
        public DynamicComponentTypeHandle dynamicType26;
        public DynamicComponentTypeHandle dynamicType27;
        public DynamicComponentTypeHandle dynamicType28;
        public DynamicComponentTypeHandle dynamicType29;
        public DynamicComponentTypeHandle dynamicType30;
        public DynamicComponentTypeHandle dynamicType31;
    }
}
