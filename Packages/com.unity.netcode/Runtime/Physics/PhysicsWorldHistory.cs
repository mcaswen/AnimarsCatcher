using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Mathematics;

[assembly: InternalsVisibleTo("Unity.NetCode.Physics.EditorTests")]
namespace Unity.NetCode
{

    /// <summary>
    /// 可用于获取先前 Tick 对应 Physics Collision World 的 Singleton Component
    /// </summary>
    public partial struct PhysicsWorldHistorySingleton : IComponentData
    {
        /// <summary>
        /// 获取指定 Tick 和插值延迟对应的 <see cref="CollisionWorld"/> 状态
        /// </summary>
        /// <param name="tick">当前正在模拟的服务器 Tick</param>
        /// <param name="interpolationDelay">以 Tick 为单位的客户端插值延迟，用于回溯并获取 tick - interpolationDelay 时的 Collision World 状态
        ///     插值延迟会在内部限制为当前 Collision History 的长度，即已保存的历史状态数量</param>
        /// <param name="physicsWorld">用于获取历史缓冲区中尚不存在的 Tick 所对应 Collision World 的 Physics World</param>
        /// <param name="collWorld">从历史记录中取得的 <see cref="CollisionWorld"/> 状态</param>
        /// <param name="expectedTick">减去 interpolationDelay 后应当获取的 Tick</param>
        /// <param name="returnedTick">因限制范围而实际获取的 Tick 索引
        /// 例如限制到最早存储的 Tick 时，此处会返回该最早 Tick
        /// <br/>将其与 expectedTick 比较，可以判断玩家的 interpolationDelay 是否过高而触发了范围限制</param>
        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, ref PhysicsWorld physicsWorld, out CollisionWorld collWorld, out NetworkTick expectedTick, out NetworkTick returnedTick)
        {
            expectedTick = tick;
            expectedTick.Subtract(interpolationDelay);
            if (!LatestStoredTick.IsValid || expectedTick.IsNewerThan(LatestStoredTick))
            {
                collWorld = physicsWorld.CollisionWorld;
                returnedTick = tick;
                return;
            }
            m_History.GetCollisionWorldFromTick(tick, interpolationDelay, out collWorld, out expectedTick, out returnedTick);
        }

        /// <inheritdoc cref="GetCollisionWorldFromTick(Unity.NetCode.NetworkTick,uint,ref Unity.Physics.PhysicsWorld,out Unity.Physics.CollisionWorld,out Unity.NetCode.NetworkTick,out Unity.NetCode.NetworkTick)"/>
        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, ref PhysicsWorld physicsWorld, out CollisionWorld collWorld)
        {
            GetCollisionWorldFromTick(tick, interpolationDelay, ref physicsWorld, out collWorld, out _, out _);
        }

        /// <summary>
        /// 返回 <see cref="CollisionHistoryBuffer"/> 中存储的最新 Tick
        /// </summary>
        public NetworkTick LatestStoredTick => m_History.m_LatestStoredTick;
        internal CollisionHistoryBufferRef m_History;

        /// <summary>
        /// 可选集合，用于手动指定需要深拷贝 Collider Blob Asset 的 <see cref="CollisionWorld.Bodies"/> 索引白名单
        /// 每一项都使用 <see cref="CollisionWorld.GetRigidBodyIndex"/> 返回的索引表示
        /// <br/>集合中不能存在重复项，也不能包含已经因为
        /// <see cref="LagCompensationConfig.DeepCopyDynamicColliders"/> 或
        /// <see cref="LagCompensationConfig.DeepCopyStaticColliders"/> 配置而进行 Collider 深拷贝的刚体索引
        /// </summary>
        /// <remarks>
        /// 如果明确知道哪些 Ghost 需要延迟补偿，可以直接在此传入它们的索引
        /// 使用 <see cref="CollisionWorld.GetRigidBodyIndex"/> 可将 Entity 映射到刚体
        /// </remarks>
        [NativeDisableContainerSafetyRestriction]
        public NativeList<int> DeepCopyRigidBodyCollidersWhitelist;

        /// <summary>
        /// 用于从历史缓冲区获取调试数据的辅助方法
        /// </summary>
        /// <param name="physicsWorld">包含历史缓冲区的 Physics World</param>
        /// <returns>历史缓冲区数据</returns>
        public unsafe string GetHistoryBufferData(ref PhysicsWorld physicsWorld)
        {
            string info = $"[PhysicsWorldHistorySingleton] Size:{m_History.m_Size} History.LastStoredTick:{LatestStoredTick.ToFixedString()}";
            if (!LatestStoredTick.IsValid) return info;

            for (uint interpolDelay = 0; interpolDelay < m_History.m_Size; interpolDelay++)
            {
                GetCollisionWorldFromTick(LatestStoredTick, interpolDelay, ref physicsWorld, out var collWorld, out var expectedTick, out var returnedTick);
                info += $"\n[tick:{LatestStoredTick.ToFixedString()}^{interpolDelay}]={returnedTick.ToFixedString()} (expected:{expectedTick.ToFixedString()}) idx:{(returnedTick.IsValid ? returnedTick.TickIndexForValidTick%m_History.m_Size : -1)}";
                info += $"  Bodies:{collWorld.Bodies.Length} (dynamic:{collWorld.DynamicBodies.Length} static:{collWorld.StaticBodies.Length})";
                if (expectedTick.IsNewerThan(LatestStoredTick)) info += "  RETURNING_LIVE_COLWORLD! ";
                if (returnedTick.IsValid && LatestStoredTick.TicksSince(returnedTick) >= m_History.m_Size) info += "  OUT_OF_BOUNDS! ";
                if (!returnedTick.IsValid || expectedTick != returnedTick) info += "  RETURN_DIFF! ";

                for (var i = 0; i < collWorld.Bodies.Length; i++)
                {
                    info += $"\n\t[{i}] ";
                    GetColliderInfo(collWorld.Bodies[i], ref info);
                }
            }

            info += "\n\t--";
            return info;

            static void GetColliderInfo(RigidBody rigidBody, ref string info)
            {
                var coll = rigidBody.Collider;
                info += $"{rigidBody.Entity} Position:{rigidBody.WorldFromBody.pos} Scale:{rigidBody.Scale} CustomTags:{rigidBody.CustomTags} {(coll.IsCreated ? $"Collider:{coll.Value.Type}" : "Collider:null")}";
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawHistoryBuffer
    {
        public const int Capacity = 32;

        public CollisionWorld world00;
        public CollisionWorld world01;
        public CollisionWorld world02;
        public CollisionWorld world03;
        public CollisionWorld world04;
        public CollisionWorld world05;
        public CollisionWorld world06;
        public CollisionWorld world07;
        public CollisionWorld world08;
        public CollisionWorld world09;
        public CollisionWorld world10;
        public CollisionWorld world11;
        public CollisionWorld world12;
        public CollisionWorld world13;
        public CollisionWorld world14;
        public CollisionWorld world15;
        public CollisionWorld world16;
        public CollisionWorld world17;
        public CollisionWorld world18;
        public CollisionWorld world19;
        public CollisionWorld world20;
        public CollisionWorld world21;
        public CollisionWorld world22;
        public CollisionWorld world23;
        public CollisionWorld world24;
        public CollisionWorld world25;
        public CollisionWorld world26;
        public CollisionWorld world27;
        public CollisionWorld world28;
        public CollisionWorld world29;
        public CollisionWorld world30;
        public CollisionWorld world31;

        public NetworkTick world00Tick;
        public NetworkTick world01Tick;
        public NetworkTick world02Tick;
        public NetworkTick world03Tick;
        public NetworkTick world04Tick;
        public NetworkTick world05Tick;
        public NetworkTick world06Tick;
        public NetworkTick world07Tick;
        public NetworkTick world08Tick;
        public NetworkTick world09Tick;
        public NetworkTick world10Tick;
        public NetworkTick world11Tick;
        public NetworkTick world12Tick;
        public NetworkTick world13Tick;
        public NetworkTick world14Tick;
        public NetworkTick world15Tick;
        public NetworkTick world16Tick;
        public NetworkTick world17Tick;
        public NetworkTick world18Tick;
        public NetworkTick world19Tick;
        public NetworkTick world20Tick;
        public NetworkTick world21Tick;
        public NetworkTick world22Tick;
        public NetworkTick world23Tick;
        public NetworkTick world24Tick;
        public NetworkTick world25Tick;
        public NetworkTick world26Tick;
        public NetworkTick world27Tick;
        public NetworkTick world28Tick;
        public NetworkTick world29Tick;
        public NetworkTick world30Tick;
        public NetworkTick world31Tick;
    }

    internal static class RawHistoryBufferExtension
    {
        public static ref CollisionWorld GetWorldAt(ref this RawHistoryBuffer buffer, int index, int size, out NetworkTick tick)
        {
            tick = NetworkTick.Invalid;
            return ref GetRefsSafe(ref buffer, index, size, ref tick, false);
        }

        public static void SetWorldAt(this ref RawHistoryBuffer buffer, int index, NetworkTick tick, int size, in CollisionWorld world)
        {
            ref var collWorldRW = ref GetRefsSafe(ref buffer, index, size, ref tick, true);
            collWorldRW = world;
        }

        private static ref CollisionWorld GetRefsSafe(ref RawHistoryBuffer buffer, int index, int size, ref NetworkTick tick, bool write)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(index >= 0 && index < size);
#endif
            switch (index)
            {
                case 00: ApplyTick(index, size, ref buffer.world00Tick, ref tick, write); return ref buffer.world00;
                case 01: ApplyTick(index, size, ref buffer.world01Tick, ref tick, write); return ref buffer.world01;
                case 02: ApplyTick(index, size, ref buffer.world02Tick, ref tick, write); return ref buffer.world02;
                case 03: ApplyTick(index, size, ref buffer.world03Tick, ref tick, write); return ref buffer.world03;
                case 04: ApplyTick(index, size, ref buffer.world04Tick, ref tick, write); return ref buffer.world04;
                case 05: ApplyTick(index, size, ref buffer.world05Tick, ref tick, write); return ref buffer.world05;
                case 06: ApplyTick(index, size, ref buffer.world06Tick, ref tick, write); return ref buffer.world06;
                case 07: ApplyTick(index, size, ref buffer.world07Tick, ref tick, write); return ref buffer.world07;
                case 08: ApplyTick(index, size, ref buffer.world08Tick, ref tick, write); return ref buffer.world08;
                case 09: ApplyTick(index, size, ref buffer.world09Tick, ref tick, write); return ref buffer.world09;
                case 10: ApplyTick(index, size, ref buffer.world10Tick, ref tick, write); return ref buffer.world10;
                case 11: ApplyTick(index, size, ref buffer.world11Tick, ref tick, write); return ref buffer.world11;
                case 12: ApplyTick(index, size, ref buffer.world12Tick, ref tick, write); return ref buffer.world12;
                case 13: ApplyTick(index, size, ref buffer.world13Tick, ref tick, write); return ref buffer.world13;
                case 14: ApplyTick(index, size, ref buffer.world14Tick, ref tick, write); return ref buffer.world14;
                case 15: ApplyTick(index, size, ref buffer.world15Tick, ref tick, write); return ref buffer.world15;
                case 16: ApplyTick(index, size, ref buffer.world16Tick, ref tick, write); return ref buffer.world16;
                case 17: ApplyTick(index, size, ref buffer.world17Tick, ref tick, write); return ref buffer.world17;
                case 18: ApplyTick(index, size, ref buffer.world18Tick, ref tick, write); return ref buffer.world18;
                case 19: ApplyTick(index, size, ref buffer.world19Tick, ref tick, write); return ref buffer.world19;
                case 20: ApplyTick(index, size, ref buffer.world20Tick, ref tick, write); return ref buffer.world20;
                case 21: ApplyTick(index, size, ref buffer.world21Tick, ref tick, write); return ref buffer.world21;
                case 22: ApplyTick(index, size, ref buffer.world22Tick, ref tick, write); return ref buffer.world22;
                case 23: ApplyTick(index, size, ref buffer.world23Tick, ref tick, write); return ref buffer.world23;
                case 24: ApplyTick(index, size, ref buffer.world24Tick, ref tick, write); return ref buffer.world24;
                case 25: ApplyTick(index, size, ref buffer.world25Tick, ref tick, write); return ref buffer.world25;
                case 26: ApplyTick(index, size, ref buffer.world26Tick, ref tick, write); return ref buffer.world26;
                case 27: ApplyTick(index, size, ref buffer.world27Tick, ref tick, write); return ref buffer.world27;
                case 28: ApplyTick(index, size, ref buffer.world28Tick, ref tick, write); return ref buffer.world28;
                case 29: ApplyTick(index, size, ref buffer.world29Tick, ref tick, write); return ref buffer.world29;
                case 30: ApplyTick(index, size, ref buffer.world30Tick, ref tick, write); return ref buffer.world30;
                case 31: ApplyTick(index, size, ref buffer.world31Tick, ref tick, write); return ref buffer.world31;
                default: throw new IndexOutOfRangeException();
            }
        }

        static void ApplyTick(int index, int size, ref NetworkTick tickRW, ref NetworkTick tickValue, bool write)
        {
            if (write) tickRW = tickValue;
            else tickValue = tickRW;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if(tickValue.IsValid)
                UnityEngine.Debug.Assert(tickValue.TickIndexForValidTick % size == index, $"{tickValue.ToFixedString()} % {size} == {index}");
#endif
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CollisionHistoryBuffer : IDisposable
    {
        public const int Capacity = RawHistoryBuffer.Capacity;
        public int Size { get; }
        public unsafe bool IsCreated => m_bufferCopyPtr != null;
        public NetworkTick LatestStoredTick { get; private set; }

        private RawHistoryBuffer m_buffer;
        [NativeDisableUnsafePtrRestriction]
        private unsafe void* m_bufferCopyPtr;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        // 用于 Job 安全检查
        private AtomicSafetyHandle m_Safety;
        // 防止访问已经释放的缓冲区
        private static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<CollisionHistoryBuffer>();
#endif

        public CollisionHistoryBuffer(int size)
        {
            if (size > Capacity)
                throw new ArgumentOutOfRangeException($"Invalid size {size}. Must be <= {Capacity}");
            if (size > 0 && !math.ispow2(size))
                throw new ArgumentOutOfRangeException($"Invalid size {size}. Must be 0, 1, or a power of 2! Recommended value:{math.ceilpow2(size)}!");
            Size = size;
            LatestStoredTick = NetworkTick.Invalid;
            var defaultWorld = default(CollisionWorld);
            m_buffer = new RawHistoryBuffer();
            for(int i=0;i<Size;++i)
            {
                m_buffer.SetWorldAt(i, NetworkTick.Invalid, size, defaultWorld);
            }

            unsafe
            {
                m_bufferCopyPtr = UnsafeUtility.Malloc(UnsafeUtility.SizeOf<RawHistoryBuffer>(), 8, Allocator.Persistent);
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_Safety = AtomicSafetyHandle.Create();
            CollectionHelper.SetStaticSafetyId<CollisionHistoryBuffer>(ref m_Safety, ref s_staticSafetyId.Data);
#endif
        }

        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, out CollisionWorld collWorld)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            // 请求的数据早于支持范围时，限制到最早的 Physics 副本
            if (interpolationDelay > Size-1)
                interpolationDelay = (uint)Size-1;
            tick.Subtract(interpolationDelay);
            if (LatestStoredTick.IsValid && tick.IsNewerThan(LatestStoredTick))
                tick = LatestStoredTick;
            var index = (int)(tick.TickIndexForValidTick % Size);
            GetCollisionWorldFromIndex(index, out collWorld);
        }

        public void DisposeIndex(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            m_buffer.GetWorldAt(index, Size, out _).Dispose();
        }

        void GetCollisionWorldFromIndex(int index, out CollisionWorld collWorld)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            collWorld = m_buffer.GetWorldAt(index, Size, out _);
        }

        [Obsolete("Prefer the more explicit CloneCollisionWorld (where args are passed by ref, and PhysicsWorldHistorySingleton is injected).")]
        public void CloneCollisionWorld(int index, in CollisionWorld collWorld, in LagCompensationConfig config = default, NetworkTick tick = default)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            if (index >= Size || index >= Capacity)
                throw new IndexOutOfRangeException($"Index {index} >= Size:{Size} or Capacity:{Capacity}!");

            // 始终释放当前位置的 World
            m_buffer.GetWorldAt(index, Size, out _).Dispose();
            m_buffer.SetWorldAt(index, tick, Size, collWorld.Clone(config.DeepCopyDynamicColliders, config.DeepCopyStaticColliders));
            if(tick.IsValid && (!LatestStoredTick.IsValid || tick.IsNewerThan(LatestStoredTick)))
                LatestStoredTick = tick;
        }

        public void CloneCollisionWorld(int index, ref CollisionWorld collWorld, ref LagCompensationConfig config, ref PhysicsWorldHistorySingleton pwhs, NetworkTick tick)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            if (index >= Size || index >= Capacity)
                throw new IndexOutOfRangeException($"Index {index} >= Size:{Size} or Capacity:{Capacity}!");

            // 始终释放当前位置的 World
            m_buffer.GetWorldAt(index, Size, out _).Dispose();
            m_buffer.SetWorldAt(index, tick, Size, collWorld.Clone(config.DeepCopyDynamicColliders, config.DeepCopyStaticColliders, pwhs.DeepCopyRigidBodyCollidersWhitelist));
            if(tick.IsValid && (!LatestStoredTick.IsValid || tick.IsNewerThan(LatestStoredTick)))
                LatestStoredTick = tick;
        }

        public unsafe CollisionHistoryBufferRef AsCollisionHistoryBufferRef()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // 先调用 CheckExistsAndThrow 防止非法访问，并返回更明确的错误
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            // 再验证写入权限
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            UnsafeUtility.AsRef<RawHistoryBuffer>(m_bufferCopyPtr) = m_buffer;
            var bufferRef = new CollisionHistoryBufferRef
            {
                m_Ptr = m_bufferCopyPtr,
                m_LatestStoredTick = LatestStoredTick,
                m_Size = Size,
            };
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            bufferRef.m_Safety = m_Safety;
            AtomicSafetyHandle.UseSecondaryVersion(ref bufferRef.m_Safety);
#endif
            return bufferRef;
        }

        public void Dispose()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
#endif
            unsafe
            {
                if (m_bufferCopyPtr != null)
                {
                    UnsafeUtility.Free(m_bufferCopyPtr, Allocator.Persistent);
                    m_bufferCopyPtr = null;
                }
                for (int i = 0; i < Size; ++i)
                {
                    m_buffer.GetWorldAt(i, Size, out _).Dispose();
                }
            }
        }
    }

    /// <summary>
    /// 对 <see cref="CollisionHistoryBuffer"/> 的安全引用
    /// 访问缓冲区时可避免复制大型 World History 数据结构，
    /// 因而可以在函数、Job 或主线程中方便地传递，而不会占用过多栈空间
    /// </summary>
    internal struct CollisionHistoryBufferRef
    {
        [NativeDisableUnsafePtrRestriction]
        unsafe internal void *m_Ptr;
        internal NetworkTick m_LatestStoredTick;
        internal int m_Size;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal AtomicSafetyHandle m_Safety;
#endif
        /// <summary>
        /// 获取指定 Tick 和插值延迟对应的 <see cref="CollisionWorld"/> 状态
        /// </summary>
        /// <param name="tick">当前正在模拟的服务器 Tick</param>
        /// <param name="interpolationDelay">以 Tick 为单位的客户端插值延迟，用于回溯并获取 tick - interpolationDelay 时的 Collision World 状态
        ///     插值延迟会在内部限制为当前 Collision History 的长度，即已保存的历史状态数量</param>
        /// <param name="collWorld">从历史记录中取得的 <see cref="CollisionWorld"/> 状态</param>
        /// <param name="expectedTick">减去 interpolationDelay 后应当获取的 Tick</param>
        /// <param name="returnedTick">因限制范围而实际获取的 Tick 索引，例如限制到最早存储的 Tick 时，此处会返回该最早 Tick</param>
        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, out CollisionWorld collWorld, out NetworkTick expectedTick, out NetworkTick returnedTick)
        {
            int ringBufferIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // 错误信息会提及 NativeArray，可能具有误导性，但至少与其他容器的行为更加一致
            // 仅依赖 CheckReadAndThrow 会得到较差的错误信息
            AtomicSafetyHandle.CheckExistsAndThrow(this.m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(this.m_Safety);
#endif
            tick.Subtract(interpolationDelay);
            expectedTick = tick;

            // 请求的数据早于支持范围时，限制到最早的 Physics 副本
            if (m_LatestStoredTick.IsValid)
            {
                if (tick.IsNewerThan(m_LatestStoredTick))
                {
                    tick = m_LatestStoredTick;
                }
                else if (m_LatestStoredTick.TicksSince(tick) >= m_Size)
                {
                    tick = m_LatestStoredTick;
                    tick.Subtract((uint) (m_Size-1));
                }
            }

            // 警告：此运算要求 m_Size 是 2 的幂，否则 TickIndexForValidTick 越过 uint.MaxValue 时会得到无效索引
            UnityEngine.Debug.Assert(math.ispow2(m_Size));
            ringBufferIndex = (int)(tick.TickIndexForValidTick % m_Size);
            unsafe
            {
                collWorld = UnsafeUtility.AsRef<RawHistoryBuffer>(m_Ptr).GetWorldAt(ringBufferIndex, m_Size, out returnedTick);
            }
        }

        /// <inheritdoc cref="GetCollisionWorldFromTick(Unity.NetCode.NetworkTick,uint,out Unity.Physics.CollisionWorld,out Unity.NetCode.NetworkTick)"/>
        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, out CollisionWorld collWorld)
        {
            GetCollisionWorldFromTick(tick, interpolationDelay, out collWorld, out _, out _);
        }
    }

    /// <summary>
    /// 为延迟补偿存储 Physics World 历史状态的系统
    /// 此系统会创建 PhysicsWorldHistorySingleton，
    /// 可通过该 Singleton 获取先前 Tick 对应的 Physics Collision World
    /// </summary>
    /// <remarks>
    /// PhysicsWorld 的克隆发生在 Physics World 构建完成后不久，
    /// 以确保 Collider BlobAssetReference 有效且能够正确复制
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PhysicsSystemGroup), OrderLast = true)]
    [BurstCompile]
    public partial struct PhysicsWorldHistory : ISystem
    {
        /// <summary>
        /// RawHistoryBuffer 可以存储的 CollisionWorld 最大数量
        /// </summary>
        /// <remarks>
        /// 延迟补偿查询超出容量允许的回溯范围时，会被限制到最早的记录
        /// 此值以前为 16
        /// </remarks>
        public const int RawHistoryBufferMaxCapacity = RawHistoryBuffer.Capacity;

        CollisionHistoryBuffer m_CollisionHistory;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LagCompensationConfig>();
            state.RequireForUpdate<NetworkId>();
            state.EntityManager.CreateEntity(ComponentType.ReadWrite<PhysicsWorldHistorySingleton>());
            SystemAPI.SetSingleton(new PhysicsWorldHistorySingleton
            {
                DeepCopyRigidBodyCollidersWhitelist = new NativeList<int>(0, Allocator.Persistent),
            });
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (m_CollisionHistory.IsCreated)
                m_CollisionHistory.Dispose();

            if (SystemAPI.TryGetSingleton(out PhysicsWorldHistorySingleton pwhs) && pwhs.DeepCopyRigidBodyCollidersWhitelist.IsCreated)
                pwhs.DeepCopyRigidBodyCollidersWhitelist.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var serverTick = networkTime.ServerTick;
            if (!serverTick.IsValid || !networkTime.IsFirstTimeFullyPredictingTick)
                return;

            var config = SystemAPI.GetSingleton<LagCompensationConfig>();
            if (!m_CollisionHistory.IsCreated)
            {
                int historySize;
                if (state.WorldUnmanaged.IsServer())
                    historySize = config.ServerHistorySize != 0 ? config.ServerHistorySize : RawHistoryBuffer.Capacity;
                else
                    historySize = config.ClientHistorySize;
                if (historySize == 0)
                    return;
                if (historySize < 0 || historySize > RawHistoryBuffer.Capacity)
                {
                    SystemAPI.GetSingleton<NetDebug>().LogWarning($"Invalid LagCompensationConfig, history size ({historySize}) must be > 0 <= {RawHistoryBuffer.Capacity}. Clamping hte value to the valid range.");
                    historySize = math.clamp(historySize, 1, RawHistoryBuffer.Capacity);
                }

                m_CollisionHistory = new CollisionHistoryBuffer(historySize);
            }

            state.CompleteDependency();

            // 需要根据是否存在 Physics 配置，从不同来源获取 Physics World
            var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;
            ref var physicsWorldHistorySingleton = ref SystemAPI.GetSingletonRW<PhysicsWorldHistorySingleton>().ValueRW;

            // 使用最近的 Physics World 填充当前 Tick 之前的所有记录，因为这些模拟使用的就是该 World
            if (!m_CollisionHistory.LatestStoredTick.IsValid)
            {
                var storeTick = serverTick;
                for (int i = 0; i < m_CollisionHistory.Size; i++)
                {
                    var index = (int)(storeTick.TickIndexForValidTick % m_CollisionHistory.Size);
                    m_CollisionHistory.CloneCollisionWorld(index, ref physicsWorld.CollisionWorld, ref config, ref physicsWorldHistorySingleton, storeTick);
                    storeTick.Decrement();
                }
            }
            else
            {
                // 为每个尚未保存的 Tick 存储一个 CollisionWorld
                var ticksToStore = serverTick.TicksSince(m_CollisionHistory.LatestStoredTick);
                if (ticksToStore <= 0) return;

                // 复制数量超过 m_CollisionHistory.Size 会覆盖本帧刚复制的 Tick，因此需要限制数量
                var startStoreTick = serverTick;
                startStoreTick.Subtract((uint) math.min(ticksToStore - 1, m_CollisionHistory.Size));

                // 存储 CollisionWorld
                for (var storeTick = startStoreTick; !storeTick.IsNewerThan(serverTick); storeTick.Increment())
                {
                    var index = (int)(storeTick.TickIndexForValidTick % m_CollisionHistory.Size);
                    m_CollisionHistory.CloneCollisionWorld(index, ref physicsWorld.CollisionWorld, ref config, ref physicsWorldHistorySingleton, storeTick);
                }

                // 注意：使用多个 Physics 子步时，每个 ServerTick 只存储第一个子步的结果
            }
            physicsWorldHistorySingleton.m_History = m_CollisionHistory.AsCollisionHistoryBufferRef();
        }
    }
}
