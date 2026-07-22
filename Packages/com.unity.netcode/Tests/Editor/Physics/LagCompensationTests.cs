#pragma warning disable CS0618 // 禁用 Entities.ForEach 的过时警告
using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Entities;
using Unity.NetCode.Tests;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.TestTools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Physics;
using Unity.Physics.Extensions;
using BoxCollider = Unity.Physics.BoxCollider;
using Collider = Unity.Physics.Collider;
using RaycastHit = Unity.Physics.RaycastHit;
using SphereCollider = Unity.Physics.SphereCollider;

namespace Unity.NetCode.Physics.Tests
{
    internal class LagCompensationTestPlayerConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddBuffer<LagCompensationTestCommand>(entity);
            baker.AddComponent(entity, new CommandDataInterpolationDelay());
            baker.AddComponent(entity, new LagCompensationTestPlayer());
            baker.AddComponent(entity, new GhostOwner());
        }
    }

    internal struct LagCompensationTestPlayer : IComponentData
    {
    }

    [NetCodeDisableCommandCodeGen]
    internal struct LagCompensationTestCommand : ICommandData, ICommandDataSerializer<LagCompensationTestCommand>
    {
        public NetworkTick Tick {get; set;}
        public float3 origin;
        public float3 direction;
        public NetworkTick lastFire;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in LagCompensationTestCommand data)
        {
            writer.WriteFloat(data.origin.x);
            writer.WriteFloat(data.origin.y);
            writer.WriteFloat(data.origin.z);
            writer.WriteFloat(data.direction.x);
            writer.WriteFloat(data.direction.y);
            writer.WriteFloat(data.direction.z);
            writer.WriteUInt(data.lastFire.SerializedData);
        }
        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in LagCompensationTestCommand data, in LagCompensationTestCommand baseline, StreamCompressionModel model)
        {
            Serialize(ref writer, state, data);
        }
        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref LagCompensationTestCommand data)
        {
            data.origin.x = reader.ReadFloat();
            data.origin.y = reader.ReadFloat();
            data.origin.z = reader.ReadFloat();
            data.direction.x = reader.ReadFloat();
            data.direction.y = reader.ReadFloat();
            data.direction.z = reader.ReadFloat();
            data.lastFire = new NetworkTick{SerializedData = reader.ReadUInt()};
        }
        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref LagCompensationTestCommand data, in LagCompensationTestCommand baseline, StreamCompressionModel model)
        {
            Deserialize(ref reader, state, ref data);
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ServerSimulation)]
    internal partial class TestAutoInGameSystem : SystemBase
    {
        BeginSimulationEntityCommandBufferSystem m_BeginSimulationCommandBufferSystem;
        EntityQuery m_PlayerPrefabQuery;
        EntityQuery m_ColliderPrefabQuery;
        protected override void OnCreate()
        {
            m_BeginSimulationCommandBufferSystem = World.GetOrCreateSystemManaged<BeginSimulationEntityCommandBufferSystem>();
            m_PlayerPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<LagCompensationTestPlayer>());
            m_ColliderPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<GhostInstance>(), ComponentType.Exclude<LagCompensationTestPlayer>());
        }
        protected override void OnUpdate()
        {
            var commandBuffer = m_BeginSimulationCommandBufferSystem.CreateCommandBuffer().AsParallelWriter();

            bool isServer = World.IsServer();
            var playerPrefab = m_PlayerPrefabQuery.ToEntityArray(Allocator.Temp)[0];
            var colliderPrefabs = m_ColliderPrefabQuery.ToEntityArray(Allocator.TempJob);
            Entities.WithNone<NetworkStreamInGame>().WithoutBurst().WithReadOnly(colliderPrefabs).ForEach((int entityInQueryIndex, Entity ent, in NetworkId id) =>
            {
                commandBuffer.AddComponent(entityInQueryIndex, ent, new NetworkStreamInGame());
                if (isServer)
                {
                    // 生成玩家以便同步到客户端
                    // 为简化测试，在玩家连接时同时生成立方体和球体
                    foreach (var colliderPrefab in colliderPrefabs)
                        commandBuffer.Instantiate(entityInQueryIndex, colliderPrefab);
                    var player = commandBuffer.Instantiate(entityInQueryIndex, playerPrefab);
                    commandBuffer.SetComponent(entityInQueryIndex, player, new GhostOwner{NetworkId = id.Value});
                    commandBuffer.SetComponent(entityInQueryIndex, ent, new CommandTarget{targetEntity = player});
                }
            }).Run();
            colliderPrefabs.Dispose();
            m_BeginSimulationCommandBufferSystem.AddJobHandleForProducer(Dependency);
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(CommandSendSystemGroup))]
    [BurstCompile]
    internal partial struct LagCompensationTestCommandCommandSendSystem : ISystem
    {
        CommandSendSystem<LagCompensationTestCommand, LagCompensationTestCommand> m_CommandSend;
        [BurstCompile]
        struct SendJob : IJobChunk
        {
            public CommandSendSystem<LagCompensationTestCommand, LagCompensationTestCommand>.SendJobData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_CommandSend.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!m_CommandSend.ShouldRunCommandJob(ref state))
                return;
            var sendJob = new SendJob{data = m_CommandSend.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_CommandSend.Query, state.Dependency);
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(CommandReceiveSystemGroup))]
    [BurstCompile]
    internal partial struct LagCompensationTestCommandCommandReceiveSystem : ISystem
    {
        CommandReceiveSystem<LagCompensationTestCommand, LagCompensationTestCommand> m_CommandRecv;
        [BurstCompile]
        struct ReceiveJob : IJobChunk
        {
            public CommandReceiveSystem<LagCompensationTestCommand, LagCompensationTestCommand>.ReceiveJobData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_CommandRecv.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var recvJob = new ReceiveJob{data = m_CommandRecv.InitJobData(ref state)};
            state.Dependency = recvJob.Schedule(m_CommandRecv.Query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal unsafe partial class LagCompensationTestCubeMoveSystem : SystemBase
    {
        internal const float DebugDrawLineDuration = 30f;
        protected  override void OnUpdate()
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            foreach(var (transRef, physicsCollider) in SystemAPI.Query<RefRW<LocalTransform>, PhysicsCollider>().WithNone<LagCompensationTestPlayer>())
            {
                var prevPos = transRef.ValueRW.Position;
                var newPos = prevPos;
                newPos.x = LagCompensationTestCommandSystem.GetDeterministicXPosition(networkTime.ServerTick);
                transRef.ValueRW.Position = newPos;

                var stepColor = Color.green;
                if (networkTime.InputTargetTick.TickIndexForValidTick % 2 == 0) stepColor.a = 0.4f;

                Debug.DrawLine(newPos, prevPos, stepColor, DebugDrawLineDuration);
                Debug.DrawLine(newPos, newPos + new float3(0, 0.5f, 0), stepColor, DebugDrawLineDuration);
                if (LagCompensationTestCommandSystem.ClientShotAction != LagCompensationTestCommandSystem.ShotType.DontShoot)
                {
                    if (physicsCollider.Value.Value.Type == ColliderType.Box)
                        DrawCube(newPos, ((BoxCollider*) physicsCollider.ColliderPtr), Color.green);
                    else if (physicsCollider.Value.Value.Type == ColliderType.Sphere)
                        DrawSphere(newPos, ((SphereCollider*) physicsCollider.ColliderPtr), Color.magenta);
                }
            }
        }

        internal static void DrawSphere(float3 pos, SphereCollider* sphereColliderPtr, Color color)
        {
            var geo = sphereColliderPtr->Geometry;
            pos -= geo.Center;
            var halfSize = geo.Radius;
            var x = new float3(halfSize, halfSize, 0);
            Debug.DrawLine(pos + x, pos - x, color, DebugDrawLineDuration);
            var y = new float3(0, halfSize, halfSize);
            Debug.DrawLine(pos + y, pos - y, color, DebugDrawLineDuration);
            var z = new float3(halfSize, 0, halfSize);
            Debug.DrawLine(pos + z, pos - z, color, DebugDrawLineDuration);
        }

        internal static void DrawCube(float3 pos, BoxCollider* boxColliderPtr, Color color)
        {
            var geo = boxColliderPtr->Geometry;
            pos -= geo.Center;
            var halfSize = geo.Size * .5f;
            var x = new float3(halfSize.x, 0, 0);
            Debug.DrawLine(pos + x, pos - x, color, DebugDrawLineDuration);
            var y = new float3(0, halfSize.y, 0);
            Debug.DrawLine(pos + y, pos - y, color, DebugDrawLineDuration);
            var z = new float3(0, 0, halfSize.z);
            Debug.DrawLine(pos + z, pos - z, color, DebugDrawLineDuration);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ServerSimulation)]
    internal unsafe partial class LagCompensationTestHitScanSystem : SystemBase
    {
        public static RaycastHit? ServerRayCastHit;
        public static RaycastHit? ClientRayCastHit;
        public static bool ServerVictimEntityStillExists;
        public static bool ClientVictimEntityStillExists;
        public static bool EnableLagCompensation = true;
        public static bool NoHitsRegistered => ServerRayCastHit == null && ClientRayCastHit == null;
        public static bool OnlyClientHitRegistered => ServerRayCastHit == null && ClientRayCastHit != null;
        public static bool BothHitsRegistered => ServerRayCastHit != null && ClientRayCastHit != null;
        public static byte ForcedInputLatencyTicks;

        protected override void OnUpdate()
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var collisionHistory = SystemAPI.GetSingleton<PhysicsWorldHistorySingleton>();
            var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;
            var isServer = World.IsServer();

            Entities
                .WithoutBurst()
                .WithReadOnly(physicsWorld)
                .WithAll<LagCompensationTestPlayer>()
                .ForEach((ref LocalTransform characterTrans, in DynamicBuffer<LagCompensationTestCommand> commands, in CommandDataInterpolationDelay delay) =>
                {
                    Assert.AreEqual(1, networkTime.SimulationStepBatchSize, "Must not be batching ticks!");
                    Assert.IsFalse(networkTime.IsCatchUpTick, "Must not be catching up!");

                    // 更新玩家位置
                    var prevPos = characterTrans.Position;
                    characterTrans.Position = LagCompensationTestCommandSystem.GetPlayersDeterministicPositionForTick(networkTime.ServerTick);

                    if (networkTime.IsFirstTimeFullyPredictingTick)
                    {
                        // 绘制移动轨迹
                        var stepColor = networkTime.InputTargetTick.TickIndexForValidTick % 2 == 0
                            ? (isServer ? Color.grey : Color.white)
                            : (isServer ? Color.black : Color.grey);
                        var offset = new float3(0, 0, isServer ? 0.05f : 0);
                        Debug.DrawLine(characterTrans.Position + offset, prevPos + offset, stepColor, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);
                        Debug.DrawLine(characterTrans.Position + offset, characterTrans.Position + offset + new float3(0, 0.5f, 0), stepColor, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);
                    }

                    // 回滚重放时不执行 Hit Scan，只在首次完整预测当前 Tick 时检测
                    if (!networkTime.IsFirstTimeFullyPredictingTick)
                        return;

                    // 当前 Tick 没有 Command 或未请求开火时不做处理
                    if (!commands.GetDataAtTick(networkTime.ServerTick, out var cmd))
                        return;
                    if (cmd.lastFire != networkTime.ServerTick)
                        return;

                    // 获取 ServerTick T 的 CollisionWorld 时，需要考虑输入实际产生于前一个渲染帧
                    const int additionalRenderDelay = 1;
                    var interpolDelay = EnableLagCompensation && isServer
                        ? delay.Delay // 此处不计 additionalRenderDelay，因为测试使用自动瞄准
                                      // 服务器端不存在额外输入延迟
                        : additionalRenderDelay;

                    var forcedInputLatencyEnabled = ForcedInputLatencyTicks > 0;
                    var (expected, margin) = (isServer, forcedInputLatencyEnabled) switch
                    {
                        // 即使启用 ForcedInputLatency，客户端默认值仍为零
                        // 因为此处检查的是预测 Tick，而不是输入采集 Tick
                        (false, _) => (0, 0),
                        // 服务器启用延迟补偿后，ForcedInputLatency 开关会显著改变延迟值
                        (true, true) => (14 - (int)ForcedInputLatencyTicks, 2),
                        (true, false) => (14, 2),
                    };
                    Assert.That(delay.Delay, Is.EqualTo(expected).Within(margin), $"CommandDataInterpolationDelay.Delay value for: EnableLagCompensation:{EnableLagCompensation}, isServer:{isServer}, ForcedInputLatencyTicks:{ForcedInputLatencyTicks} ({forcedInputLatencyEnabled})!");

                    // 根据当前预测 Tick 和连接插值延迟取得对应的历史 CollisionWorld
                    collisionHistory.GetCollisionWorldFromTick(networkTime.ServerTick, interpolDelay, ref physicsWorld, out var collWorld, out var expectedTick, out var returnedTick);
                    var rayInput = new Unity.Physics.RaycastInput();
                    rayInput.Start = characterTrans.Position; // 此处刻意不使用 Command 中的射线起点
                    var positionDesyncMeters = math.distance(rayInput.Start, cmd.origin);
                    rayInput.End = rayInput.Start + cmd.direction;
                    rayInput.Filter = Unity.Physics.CollisionFilter.Default;

                    bool hit = collWorld.CastRay(rayInput, out var raycastHit);
                    var color = isServer ? Color.blue : Color.red;
                    Debug.DrawLine(characterTrans.Position, rayInput.End, color, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);

                    // 绘制淡色射线表示客户端原始开火位置，以展示 ForcedInputLatency 造成的偏差
                    {
                        var black = Color.black;
                        black.a = 0.2f;
                        Debug.DrawLine(cmd.origin, cmd.origin + cmd.direction, black, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);
                    }

                    var victimIsAlive = EntityManager.Exists(raycastHit.Entity);
                    FixedString512Bytes networkTickInfo = $"\n{networkTime.ToFixedString()}";
                    string collisionInfo = hit ? $" - {collWorld.Bodies[raycastHit.RigidBodyIndex].Collider.Value.Type}!\n\traycastHit[Entity: {raycastHit.Entity} (alive: {victimIsAlive}), Position: {raycastHit.Position}, SurfaceNormal: {raycastHit.SurfaceNormal}, Fraction: {raycastHit.Fraction}, ColliderKey: {raycastHit.ColliderKey.ToString()}, RigidBodyIndex: {raycastHit.RigidBodyIndex}, Material.Friction: {raycastHit.Material.Friction}]" : "";
                    collisionInfo = $"[TickIndex:{NetCodeTestWorld.TickIndex}][ServerTick:{networkTime.ServerTick.ToFixedString()}] LagCompensationTest result on <color=green>{(isServer ? "SERVER" : "CLIENT")}</color> is {(hit ? $"<color=green>HIT</color> (index: {raycastHit.RigidBodyIndex})" : "<color=red>MISS</color>")} on ServerTick {cmd.Tick.ToFixedString()} with interpolDelay: {interpolDelay} ticks (historyBufferEntry[expectedTick:{expectedTick}, returnedTick:{returnedTick.ToFixedString()}]), and origin desync of: {positionDesyncMeters}m!\n\tRay(start: {rayInput.Start} vs cmd.origin: {cmd.origin}, end: {rayInput.End}, dir: {(rayInput.End - rayInput.Start)}, range: {math.length(cmd.direction):0.00}m)! {networkTickInfo} {collisionInfo}\n";
                    if (hit)
                    {
                        if (isServer)
                            ServerRayCastHit = raycastHit;
                        else ClientRayCastHit = raycastHit;

                        // 即使实体随后已被删除，历史 CollisionWorld 查询仍应返回当时的 Entity
                        if (isServer)
                            ServerVictimEntityStillExists = victimIsAlive;
                        else ClientVictimEntityStillExists = victimIsAlive;

                        var victimCollider = collWorld.Bodies[raycastHit.RigidBodyIndex].Collider;
                        Assert.IsTrue(victimCollider.IsCreated, "Expecting physics collider in historic collision world to be valid, due to deep copy clone operation!");
                    }

                    // 绘制历史 CollisionWorld 中的全部 Collider
                    for (var i = 0; i < collWorld.Bodies.Length; i++)
                    {
                        var rigidBody = collWorld.Bodies[i];
                        var victimPos = rigidBody.WorldFromBody.pos;
                        var victimCollider = rigidBody.Collider;
                        if (!victimCollider.IsCreated)
                        {
                            collisionInfo += $"\n\tcollWorld.Bodies[{i}] Pos:{victimPos} null";
                            continue;
                        }
                        var drawOffset = new float3(0, 0, 0.001f); // 略微偏移以免与其他调试线重叠
                        if (victimCollider.Value.Type == ColliderType.Box)
                        {
                            var boxCollider = ((BoxCollider*) victimCollider.GetUnsafePtr());
                            LagCompensationTestCubeMoveSystem.DrawCube(victimPos + drawOffset, boxCollider, color);
                            collisionInfo += $"\n\tcollWorld.Bodies[{i}] BoxCollider Pos:{victimPos} Geometry.Size:{boxCollider->Geometry.Size}";
                        }
                        else if (victimCollider.Value.Type == ColliderType.Sphere)
                        {
                            var sphereCollider = ((SphereCollider*) victimCollider.GetUnsafePtr());
                            LagCompensationTestCubeMoveSystem.DrawSphere(victimPos + drawOffset, sphereCollider, color);
                            collisionInfo += $"\n\tcollWorld.Bodies[{i}] SphereCollider Pos:{victimPos} Geometry.Radius:{sphereCollider->Geometry.Radius}";
                        }
                        else Assert.Fail("Sanity check");
                    }

                    collisionInfo += $"\n\n{collisionHistory.GetHistoryBufferData(ref physicsWorld)}";
                    Debug.Log(collisionInfo);
                }).Run();
        }
    }
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [AlwaysSynchronizeSystem]
    [DisableAutoCreation]
    internal partial class LagCompensationTestCommandSystem : SystemBase
    {
        internal enum ShotType
        {
            DontShoot = default,
            ShootToHit,
            ShootToMiss,
        }
        public static ShotType ClientShotAction;
        public static Entity ClientAimAtTarget;

        protected override void OnCreate()
        {
            RequireForUpdate<CommandTarget>();
        }
        protected override void OnUpdate()
        {
            var target = SystemAPI.GetSingleton<CommandTarget>();
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (target.targetEntity == Entity.Null)
            {
                foreach (var (_, entity) in SystemAPI.Query<RefRO<PredictedGhost>>().WithEntityAccess().WithAll<LagCompensationTestPlayer>())
                {
                    target.targetEntity = entity;
                    SystemAPI.SetSingleton(target);
                }
            }
            if (target.targetEntity == Entity.Null || !networkTime.ServerTick.IsValid || !EntityManager.HasComponent<LagCompensationTestCommand>(target.targetEntity))
                return;

            var buffer = EntityManager.GetBuffer<LagCompensationTestCommand>(target.targetEntity);
            var cmd = default(LagCompensationTestCommand);
            cmd.Tick = networkTime.InputTargetTick;
            if (ClientShotAction != ShotType.DontShoot)
            {
                foreach (var localTransform in SystemAPI.Query<LocalTransform>().WithAll<LagCompensationTestPlayer>())
                {
                    // 此处不能使用玩家实体当前的 LocalTransform.Position，因为该值尚未更新
                    // GhostInputSystemGroup 运行在 GhostUpdateSystem 和预测循环之前
                    cmd.origin = GetPlayersDeterministicPositionForTick(networkTime.ServerTick);

                    var victimTransform = EntityManager.GetComponentData<LocalTransform>(ClientAimAtTarget);
                    var aimPoint = victimTransform.Position;
                    var isTryingToMiss = ClientShotAction == ShotType.ShootToMiss;
                    if (isTryingToMiss) aimPoint.y += 2.5f; // 瞄准目标上方以强制射偏
                    cmd.direction = (aimPoint - cmd.origin) * 1.1f; // 将射线距离增加百分之十
                    cmd.lastFire = cmd.Tick;

                    Debug.DrawLine(cmd.origin, aimPoint, Color.yellow, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);
                    Debug.Log($"<color=yellow>[TickIndex:{NetCodeTestWorld.TickIndex}][ServerTick:{networkTime.ServerTick.ToFixedString()}] Client aiming at {ClientAimAtTarget.ToFixedString()} and pressing shoot once: From {cmd.origin} (vs deterministic: {GetPlayersDeterministicPositionForTick(networkTime.InputTargetTick)}), to: {victimTransform.Position}, thus direction {cmd.direction}, with goal '{ClientShotAction}'!</color>");
                    ClientShotAction = default;
                }
            }
            // 未开火且当前 Tick 已有数据时跳过，避免已有 Command 被覆盖
            else if (buffer.GetDataAtTick(cmd.Tick, out var dupCmd) && dupCmd.Tick == cmd.Tick)
                return;
            buffer.AddCommandData(cmd);
        }

        internal static float3 GetPlayersDeterministicPositionForTick(NetworkTick targetTick)
        {
            return new float3(GetDeterministicXPosition(targetTick), 0, -10);
        }

        internal static float GetDeterministicXPosition(NetworkTick targetTick)
        {
            return (targetTick.TickIndexForValidTick * LagCompensationTests.MovementSpeedPerTick);
        }
    }

    internal class LagCompensationTests
    {
        const int k_TicksToRegisterHit = 12;

        // 使用易于区分的数值方便调试
        internal static float BoxColliderGeometryOriginalSize = 0.222f;
        private static float BoxColliderGeometryResizeSize = 0.333f;
        private static float SphereColliderRadiusSize = 0.4444f;
        internal static float MovementSpeedPerTick = 0.5f; // 每 Tick 位移大于各 Collider 直径
                                                           // 因而命中必须依赖准确的历史位置

        [Test]
        public void LagCompensationDoesNotUpdateIfLagCompensationConfigIsNotPresent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 50; // 单向延迟五十毫秒，因此往返延迟至少一百毫秒
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Physics,");
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.Physics,");
                testWorld.Bootstrap(true);

                testWorld.CreateWorlds(true, 1, false);
                Assert.IsFalse(testWorld.TryGetSingletonEntity<LagCompensationConfig>(testWorld.ServerWorld) != Entity.Null);
                Assert.IsFalse(testWorld.TryGetSingletonEntity<LagCompensationConfig>(testWorld.ClientWorlds[0]) != Entity.Null);
                testWorld.Connect(maxSteps: 16);

                var serverPhy = testWorld.GetSingleton<PhysicsWorldHistorySingleton>(testWorld.ServerWorld);
                Assert.AreEqual(NetworkTick.Invalid, serverPhy.LatestStoredTick);
                var clientPhy = testWorld.GetSingleton<PhysicsWorldHistorySingleton>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(NetworkTick.Invalid, clientPhy.LatestStoredTick);
            }
        }

        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitAndMissWithLagCompensation()
        {
            LagCompensationTestHitScanSystem.ForcedInputLatencyTicks = 0;
            HitAndMissWithLagCompensationTest();
        }

        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitAndMissWithLagCompensation_AndForcedInputLatency_Of4()
        {
            LagCompensationTestHitScanSystem.ForcedInputLatencyTicks = 4;
            HitAndMissWithLagCompensationTest();
        }

        public void HitAndMissWithLagCompensationTest()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                InitTest(testWorld, false, IncrementalBroadphase.FullBVHRebuild, out var clientEm, out _, new LagCompensationConfig
                {
                    ServerHistorySize = PhysicsWorldHistory.RawHistoryBufferMaxCapacity / 2,
                    ClientHistorySize = 2,
                    DeepCopyDynamicColliders = true,
                    DeepCopyStaticColliders = true,
                });
                var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
                clientTickRate.ForcedInputLatencyTicks = LagCompensationTestHitScanSystem.ForcedInputLatencyTicks;
                testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);

                // 等待实体生成并让网络时间同步稳定
                for (int i = 0; i < 70; ++i)
                    testWorld.Tick();

                GetCubeAndSphere(clientEm, out var clientVictimCubeEntity, out _, out _, out _);
                LagCompensationTestCommandSystem.ClientAimAtTarget = clientVictimCubeEntity;
                LagCompensationTestHitScanSystem.EnableLagCompensation = true;
                int ticksToRegisterHit = k_TicksToRegisterHit + LagCompensationTestHitScanSystem.ForcedInputLatencyTicks;

                // 测试命中
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToHit;
                for (int i = 0; i < ticksToRegisterHit; ++i)
                    testWorld.Tick();
                Assert.IsTrue(LagCompensationTestHitScanSystem.BothHitsRegistered);

                // 测试射偏
                ResetHits();
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToMiss;
                for (int i = 0; i < ticksToRegisterHit; ++i)
                    testWorld.Tick();
                Assert.IsTrue(LagCompensationTestHitScanSystem.NoHitsRegistered);

                // 验证禁用延迟补偿后服务器无法命中历史位置
                ResetHits();
                LagCompensationTestHitScanSystem.EnableLagCompensation = false;
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToHit;
                for (int i = 0; i < ticksToRegisterHit; ++i)
                    testWorld.Tick();
                Assert.IsTrue(LagCompensationTestHitScanSystem.OnlyClientHitRegistered);

                // 再次测试射偏
                ResetHits();
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToMiss;
                for (int i = 0; i < ticksToRegisterHit; ++i)
                    testWorld.Tick();
                Assert.IsTrue(LagCompensationTestHitScanSystem.NoHitsRegistered);
            }
        }

        internal enum ColliderChangeType
        {
            NoColliderChange,
            ResizeCollider,
            ChangeColliderToSphere,
            ColliderMakeUnique,
        }
        internal enum DestroyType
        {
            DestroyVictimEntity,
            KeepVictimEntityAlive,
        }
        internal enum DeepCopyStrategy
        {
            DeepCopyOnlyDynamic,
            DeepCopyOnlyStatic,
            OnlyManualWhitelist,
            DeepCopyBoth,
            DeepCopyNeither,
        }
        internal enum ColliderStaticType
        {
            StaticVictimEntity,
            DynamicVictimEntity,
        }
        internal enum ColliderChangeTiming
        {
            ColliderChangeBeforeShot,
            ColliderChangeAfterShot,
        }
        internal enum IncrementalBroadphase
        {
            FullBVHRebuild,
            IncrementalBVH,
        }

        /// <summary>
        /// 客户问题：延迟补偿命中随后已销毁的 Entity 时会抛出 BlobAsset 异常
        /// https://docs.google.com/document/d/18RZrbZfAwD37J2goBPODvqTcH9jkwCyeQN5wlmMqGVk/edit
        /// DOTS-10392
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitWithLagCompensationWithColliderChangeBeforeShot([Values]IncrementalBroadphase incrementalBroadphase, [Values]ColliderStaticType victimColliderType, [Values]DestroyType destroyType, [Values]DeepCopyStrategy deepCopyStrategy, [Values] ColliderChangeType colliderChangeType)
        {
            RunHitWithLagCompensationWithColliderChangeTest(incrementalBroadphase, ColliderChangeTiming.ColliderChangeBeforeShot, victimColliderType, destroyType, deepCopyStrategy, colliderChangeType);
        }

        /// <summary>
        /// 客户问题：延迟补偿命中随后已销毁的 Entity 时会抛出 BlobAsset 异常
        /// https://docs.google.com/document/d/18RZrbZfAwD37J2goBPODvqTcH9jkwCyeQN5wlmMqGVk/edit
        /// DOTS-10392
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitWithLagCompensationWithColliderChangeAfterShot([Values]IncrementalBroadphase incrementalBroadphase, [Values] ColliderStaticType victimColliderType, [Values] DestroyType destroyType, [Values] DeepCopyStrategy deepCopyStrategy, [Values] ColliderChangeType colliderChangeType)
        {
            RunHitWithLagCompensationWithColliderChangeTest(incrementalBroadphase, ColliderChangeTiming.ColliderChangeAfterShot, victimColliderType, destroyType, deepCopyStrategy, colliderChangeType);
        }

        private static void RunHitWithLagCompensationWithColliderChangeTest(IncrementalBroadphase incrementalBroadphase, ColliderChangeTiming colliderChangeTiming, ColliderStaticType victimColliderType, DestroyType destroyType, DeepCopyStrategy deepCopyStrategy, ColliderChangeType colliderChangeType)
        {
            // TODO 增加基于统计的测试，例如开火一千次
            // TODO 验证模拟期间插值延迟变化时的行为
            // TODO 使用带退化情况的可变时间步
            using (var testWorld = new NetCodeTestWorld())
            {
                var config = new LagCompensationConfig
                {
                    ServerHistorySize = PhysicsWorldHistory.RawHistoryBufferMaxCapacity,
                    ClientHistorySize = 2,
                    DeepCopyDynamicColliders = deepCopyStrategy is DeepCopyStrategy.DeepCopyOnlyDynamic or DeepCopyStrategy.DeepCopyBoth,
                    DeepCopyStaticColliders = deepCopyStrategy is DeepCopyStrategy.DeepCopyOnlyStatic or DeepCopyStrategy.DeepCopyBoth,
                };
                InitTest(testWorld, victimColliderType == ColliderStaticType.StaticVictimEntity, incrementalBroadphase, out var clientEm, out var serverEm, config);

                // 等待实体生成并让网络时间同步稳定
                for (int i = 0; i < 20; ++i)
                    testWorld.Tick();

                // 取得 LagCompensationTestCube 和 LagCompensationTestSphere 实体
                GetCubeAndSphere(serverEm, out var serverVictimCubeEntity, out var serverVictimCollider, out var serverSphereEntity, out var serverSphereCollider);
                GetCubeAndSphere(clientEm, out var clientVictimCubeEntity, out var clientVictimCollider, out var clientSphereEntity, out var clientSphereCollider);

                // 刚体生成后将其加入深拷贝白名单
                {
                    var serverBodies = testWorld.GetSingletonRW<PhysicsWorldSingleton>(testWorld.ServerWorld).ValueRW.Bodies;
                    var clientBodies = testWorld.GetSingletonRW<PhysicsWorldSingleton>(testWorld.ClientWorlds[0]).ValueRW.Bodies;
                    ref var serverWhitelist = ref testWorld.GetSingletonRW<PhysicsWorldHistorySingleton>(testWorld.ServerWorld).ValueRW.DeepCopyRigidBodyCollidersWhitelist;
                    ref var clientWhitelist = ref testWorld.GetSingletonRW<PhysicsWorldHistorySingleton>(testWorld.ClientWorlds[0]).ValueRW.DeepCopyRigidBodyCollidersWhitelist;
                    AddBodiesToWhitelist("Server", serverBodies, ref serverWhitelist, serverVictimCubeEntity, serverSphereEntity, deepCopyStrategy);
                    AddBodiesToWhitelist("Client", clientBodies, ref clientWhitelist, clientVictimCubeEntity, clientSphereEntity, deepCopyStrategy);
                    static void AddBodiesToWhitelist(string context, NativeArray<RigidBody> bodies, ref NativeList<int> whitelist, Entity victimCubeEntity, Entity sphereEntity, DeepCopyStrategy deepCopyStrategy)
                    {
                        // Bodies 列表可能包含空项，因此数量可以超过三
                        Assert.That(bodies.Length, Is.GreaterThanOrEqualTo(3), $"Sanity - PhysicsWorld Bodies count on {context}!");
                        if (deepCopyStrategy is not DeepCopyStrategy.OnlyManualWhitelist) return;
                        for (var bodyIdx = 0; bodyIdx < bodies.Length; bodyIdx++)
                        {
                            if (bodies[bodyIdx].Entity == victimCubeEntity || bodies[bodyIdx].Entity == sphereEntity)
                                whitelist.Add(bodyIdx);
                        }
                        Assert.That(bodies.Length, Is.GreaterThanOrEqualTo(2), $"Sanity! {context} must have bodies!");
                    }
                }

                // 无论双方时间线如何，都在客户端和服务器上应用相同 Collider 变化以模拟复制
                if (colliderChangeTiming == ColliderChangeTiming.ColliderChangeBeforeShot)
                {
                    PredictColliderChanges(colliderChangeType, serverEm, serverVictimCubeEntity, ref serverVictimCollider, serverSphereCollider);
                    PredictColliderChanges(colliderChangeType, clientEm, clientVictimCubeEntity, ref clientVictimCollider, clientSphereCollider);
                }

                // 继续等待网络时间同步稳定
                // 同时让上方 Collider 更新应用对应的深拷贝策略
                for (int i = 0; i < 50; ++i)
                    testWorld.Tick();

                // 客户端开火
                LagCompensationTestHitScanSystem.ForcedInputLatencyTicks = 0;
                LagCompensationTestCommandSystem.ClientAimAtTarget = clientVictimCubeEntity;
                LagCompensationTestHitScanSystem.EnableLagCompensation = true;
                Assert.IsTrue(LagCompensationTestHitScanSystem.NoHitsRegistered, "Sanity check: Neither client nor server should have hit anything yet.");
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToHit;
                // 模拟延迟使客户端开火输入在后续帧才到达服务器

                testWorld.Tick();
                testWorld.Tick(); // 客户端确认命中的 Tick
                Assert.IsTrue(LagCompensationTestHitScanSystem.OnlyClientHitRegistered, "Sanity check: Expected the client shot to have landed by now.");

                testWorld.Tick();
                if (colliderChangeTiming == ColliderChangeTiming.ColliderChangeAfterShot)
                {
                    // 此时只修改客户端，因为客户端时间领先服务器且这里模拟预测产生的 Collider 变化
                    // 若客户端在 T5 预测变化，即开火前两帧，服务器随后也会在自己的 T5 执行同一变化
                    // 本测试不覆盖输入不确定性导致 Collider 尺寸预测错误的情况，该情况按定义会失败
                    PredictColliderChanges(colliderChangeType, clientEm, clientVictimCubeEntity, ref clientVictimCollider, clientSphereCollider);
                }

                // 在服务器删除 LagCompensationTestCube
                // 模拟延迟使客户端开火输入在后续帧才到达服务器
                if (destroyType == DestroyType.DestroyVictimEntity)
                {
                    //Debug.Log($"Destroying victim entity: {serverVictimCubeEntity} to trigger Physics BlobAsset bug...");
                    serverEm.DestroyEntity(serverVictimCubeEntity);

                    // HACK 由于存在 ICleanupComponentData，销毁实体是多阶段过程
                    // GhostDespawnParallelJob 会等待所有客户端确认包含删除信息的快照后才真正删除实体
                    // 该过程需要多个 Tick，正常测试必须更早开始销毁并精确安排客户端 ACK 到达顺序
                    // 这会使测试脆弱且难以推理，因此这里手动移除 GhostCleanup 以强制完成删除

                    // 客户端仍可能上报命中随后已删除的 Ghost，延迟删除只会降低发生概率
                    // 在 N4E 规模下低概率事件仍会频繁出现

                    serverEm.RemoveComponent<GhostCleanup>(serverVictimCubeEntity);
                }

                // 服务器在历史 CollisionWorld 上执行命中检测
                // 启用深拷贝时必须返回变化前的 Collider
                for (int i = 0; i < k_TicksToRegisterHit; ++i)
                {
                    testWorld.Tick();
                }

                Assert.IsTrue(LagCompensationTestHitScanSystem.BothHitsRegistered, "Sanity: Expected the hit to have registered now on BOTH the client and server!");
                switch (destroyType)
                {
                    case DestroyType.DestroyVictimEntity:
                        Assert.IsTrue(LagCompensationTestHitScanSystem.ClientVictimEntityStillExists, "Sanity: Expected only the client to hit an ALIVE entity, and server a dead one!");
                        Assert.IsFalse(LagCompensationTestHitScanSystem.ServerVictimEntityStillExists, "Sanity: Expected only the client to hit an ALIVE entity, and server a dead one!");
                        break;
                    case DestroyType.KeepVictimEntityAlive:
                        Assert.IsTrue(LagCompensationTestHitScanSystem.ClientVictimEntityStillExists, "Sanity: Expected both entities to be alive!");
                        Assert.IsTrue(LagCompensationTestHitScanSystem.ServerVictimEntityStillExists, "Sanity: Expected both entities to be alive!");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(destroyType), destroyType, null);
                }

                if (colliderChangeTiming == ColliderChangeTiming.ColliderChangeAfterShot)
                {
                    // 确认服务器命中后，服务器再模拟调整 Collider 的 Tick
                    if(serverEm.Exists(serverVictimCubeEntity))
                        PredictColliderChanges(colliderChangeType, serverEm, serverVictimCubeEntity, ref serverVictimCollider, serverSphereCollider);
                }

                // 即使 Entity 已销毁，历史碰撞命中仍应返回原 Entity
                // 因此检查命中实体与受击实体一致
                Assert.AreEqual(clientVictimCubeEntity, LagCompensationTestHitScanSystem.ClientRayCastHit.Value.Entity, "Expecting to hit the client victim entity!");
                var serverRayCastHit = LagCompensationTestHitScanSystem.ServerRayCastHit.Value;
                Assert.AreEqual(serverVictimCubeEntity, serverRayCastHit.Entity, "Expecting to hit the server victim entity!");

                // 同时验证命中信息基本确定
                var hitDistance = math.length(serverRayCastHit.Position - LagCompensationTestHitScanSystem.ClientRayCastHit.Value.Position);
                var hitRayFraction = math.length(serverRayCastHit.Fraction - LagCompensationTestHitScanSystem.ClientRayCastHit.Value.Fraction);
                var hitNormalDot = math.dot(serverRayCastHit.SurfaceNormal, LagCompensationTestHitScanSystem.ClientRayCastHit.Value.SurfaceNormal);
                Debug.Log($"ServerRayCastHit vs ClientRayCastHit: hitDistance: {hitDistance}, hitRayFraction: {hitRayFraction}, hitNormalDot: {hitNormalDot}!");

                // 比较服务器与客户端命中结果以验证其基本确定性
                // 只有正确深拷贝对应 Collider 类型时结果才有明确定义
                // 未深拷贝场景的历史 Collider 行为不作保证
                var isCopyingTheRightTypeOfCollider = victimColliderType == ColliderStaticType.StaticVictimEntity
                    ? config.DeepCopyStaticColliders
                    : config.DeepCopyDynamicColliders;
                var isDeepCopyingCorrectly = deepCopyStrategy is DeepCopyStrategy.DeepCopyBoth or DeepCopyStrategy.OnlyManualWhitelist
                                             || isCopyingTheRightTypeOfCollider;
                if (isDeepCopyingCorrectly)
                {
                    AssertInRange(hitDistance, 0f, allowedTolerance: 0.1f, "RayCastHit.Position between the hit on the client, and the hit on the server!");
                    AssertInRange(hitRayFraction, 0f, allowedTolerance: 0.02f, "RayCastHit.Fraction (i.e. ray.distance / ray.length) between the hit on the client, and the hit on the server!");
                    AssertInRange(hitNormalDot, 1f, allowedTolerance: 0.02f, "RayCastHit.SurfaceNormal between the hit on the client, and the hit on the server!");
                }

                static void AssertInRange(float testedValue, float expectedValue, float allowedTolerance, string reasoning)
                {
                    var rawDelta = testedValue - expectedValue;
                    var isInBounds = math.abs(rawDelta) <= allowedTolerance;
                    if (!isInBounds)
                    {
                        reasoning = $"Expected {testedValue} to BE WITHIN {expectedValue}±{allowedTolerance}, but it wasn't! Value was {testedValue} (a delta of ?{rawDelta})! " + reasoning;
                        Assert.Fail(reasoning);
                    }
                }
            }
        }

        internal static unsafe void PredictColliderChanges(ColliderChangeType colliderChangeType, EntityManager em, Entity victimCubeEntity, ref PhysicsCollider victimCollider, PhysicsCollider sphereCollider)
        {
            // 参考 https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/PhysicsSamples/Assets/9.%20Modify
            // Collider 变化必须参与预测
            // 客户端和服务器要在相同 ServerTick 上以相同方式修改受击方 BoxCollider

            em.CompleteAllTrackedJobs(); // 完成依赖以避免安全检查冲突
            switch (colliderChangeType)
            {
                case ColliderChangeType.NoColliderChange:
                    break;
                case ColliderChangeType.ResizeCollider:
                    // 调整所有共享该 BlobAsset Geometry 的 BoxCollider
                    var boxCollider = ((BoxCollider*) victimCollider.ColliderPtr);
                    var boxGeometry = boxCollider->Geometry;
                    boxGeometry.Size = BoxColliderGeometryResizeSize;
                    boxCollider->Geometry = boxGeometry;
                    break;
                case ColliderChangeType.ChangeColliderToSphere:
                    // 只更改当前 Collider 的类型
                    victimCollider.Value = sphereCollider.Value;
                    em.SetComponentData(victimCubeEntity, victimCollider);
                    break;
                case ColliderChangeType.ColliderMakeUnique:
                    victimCollider.MakeUnique(victimCubeEntity, em);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(colliderChangeType), colliderChangeType, null);
            }

            victimCollider = em.GetComponentData<PhysicsCollider>(victimCubeEntity);
            em.CompleteAllTrackedJobs(); // 完成依赖以避免安全检查冲突
        }

        private static void GetCubeAndSphere(EntityManager em, out Entity victimCubeEntity, out Unity.Physics.PhysicsCollider victimCollider, out Entity sphereEntity, out Unity.Physics.PhysicsCollider sphereCollider)
        {
            using var colliderQuery = em.CreateEntityQuery(ComponentType.ReadWrite<Unity.Physics.PhysicsCollider>());
            var colliderEntities = colliderQuery.ToEntityArray(Allocator.Temp);
            var colliderColliders = colliderQuery.ToComponentDataArray<Unity.Physics.PhysicsCollider>(Allocator.Temp);
            victimCubeEntity = colliderEntities[0];
            sphereEntity = colliderEntities[1];
            victimCollider = colliderColliders[0];
            sphereCollider = colliderColliders[1];

            Assert.IsTrue(victimCollider.IsValid);
            Assert.IsTrue(sphereCollider.IsValid);
            // 查询返回顺序不确定，若类型相反则交换实体和 Collider
            if (victimCollider.Value.Value.Type != ColliderType.Box)
            {
                (victimCollider, sphereCollider) = (sphereCollider, victimCollider);
                (victimCubeEntity, sphereEntity) = (sphereEntity, victimCubeEntity);
            }
            Assert.AreEqual(ColliderType.Box, victimCollider.Value.Value.Type);
            Assert.AreEqual(ColliderType.Sphere, sphereCollider.Value.Value.Type);
        }

        private static void InitTest(NetCodeTestWorld testWorld, bool useStaticColliders, IncrementalBroadphase broadphaseMode, out EntityManager clientEm, out EntityManager serverEm, LagCompensationConfig config)
        {
            testWorld.DriverSimulatedDelay = 50; // 单向延迟五十毫秒，因此往返延迟至少一百毫秒
            testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Physics,");
            testWorld.TestSpecificAdditionalAssemblies.Add("Unity.Physics,");

            testWorld.Bootstrap(true,
                typeof(TestAutoInGameSystem),
                typeof(LagCompensationTestCubeMoveSystem),
                typeof(LagCompensationTestCommandCommandSendSystem),
                typeof(LagCompensationTestCommandCommandReceiveSystem),
                typeof(LagCompensationTestCommandSystem),
                typeof(LagCompensationTestHitScanSystem));

            var cubeGameObject = new GameObject("LagCompensationTestCube");
            cubeGameObject.AddComponent<UnityEngine.BoxCollider>().size = new Vector3(BoxColliderGeometryOriginalSize, BoxColliderGeometryOriginalSize, BoxColliderGeometryOriginalSize);
            var sphereGameObject = new GameObject("LagCompensationTestSphere");
            sphereGameObject.transform.position = new Vector3(0, -5, 0); // 沿 Y 轴移开球体以免干扰射线
            sphereGameObject.AddComponent<UnityEngine.SphereCollider>().radius = SphereColliderRadiusSize;
            var playerGameObject = new GameObject("LagCompensationTestPlayer");
            playerGameObject.transform.position = new Vector3(0, 0, 0);
            playerGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LagCompensationTestPlayerConverter();
            var ghostAuth = playerGameObject.AddComponent<GhostAuthoringComponent>();
            ghostAuth.DefaultGhostMode = GhostMode.OwnerPredicted;

            if (!useStaticColliders) cubeGameObject.AddComponent<Rigidbody>().useGravity = false;
            if (!useStaticColliders) sphereGameObject.AddComponent<Rigidbody>().useGravity = false;
            if (!useStaticColliders) playerGameObject.AddComponent<Rigidbody>().useGravity = false;

            Assert.IsTrue(testWorld.CreateGhostCollection(playerGameObject, cubeGameObject, sphereGameObject));

            testWorld.CreateWorlds(true, 1);

            serverEm = testWorld.ServerWorld.EntityManager;
            clientEm = testWorld.ClientWorlds[0].EntityManager;
            serverEm.CreateSingleton(config);
            clientEm.CreateSingleton(config);
            var step = PhysicsStep.Default;
            step.IncrementalStaticBroadphase = broadphaseMode == IncrementalBroadphase.IncrementalBVH;
            step.IncrementalDynamicBroadphase = broadphaseMode == IncrementalBroadphase.IncrementalBVH;
            step.MultiThreaded = 0;
            step.SimulationType = SimulationType.UnityPhysics;
            step.SolverIterationCount = 1;
            serverEm.CreateSingleton(step);
            clientEm.CreateSingleton(step);
            testWorld.Connect(maxSteps: 32);

            ResetHits();
        }

        private static void ResetHits()
        {
            LagCompensationTestHitScanSystem.ClientRayCastHit = default;
            LagCompensationTestHitScanSystem.ServerRayCastHit = default;
            LagCompensationTestHitScanSystem.ClientVictimEntityStillExists = default;
            LagCompensationTestHitScanSystem.ServerVictimEntityStillExists = default;
            LagCompensationTestCommandSystem.ClientShotAction = default;
        }
    }
}
