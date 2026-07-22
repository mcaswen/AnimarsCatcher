using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;
using Object = UnityEngine.Object;

namespace Unity.NetCode.Tests
{
    internal class TestEnterExitGame : TestWithSceneAsset
    {
        private void UnloadSubScene(World world)
        {
            var subScene = Object.FindFirstObjectByType<SubScene>();
            SceneSystem.UnloadScene(world.Unmanaged, subScene.SceneGUID, SceneSystem.UnloadParameters.DestroyMetaEntities);
        }

        [Test]
        public unsafe void PrespawnSystemResetWhenExitGame()
        {
            const int numClients = 2;
            const int numObjects = 10;
            var prefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "simple", typeof(GhostAuthoringComponent));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "TestEnterExit");
            var subScene = SubSceneHelper.CreateSubSceneWithPrefabs(parentScene, Path.GetDirectoryName(parentScene.path), "SubScene",
                new[] {prefab}, numObjects);
            using (var testWorld = new NetCodeTestWorld())
            {
                // 创建包含多个对象的 SubScene
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, numClients);
                // 流式加载 SubScene
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                var firstTimeJoinStats = new uint[testWorld.ClientWorlds.Length * 3];
                var rejoinStats = new uint[testWorld.ClientWorlds.Length * 3];
                testWorld.GoInGame();
                int firstJoinTickCount = 0;
                int rejoinTickCount = 0;
                for(int i=0;i<32;++i)
                {
                    ++firstJoinTickCount;
                    testWorld.Tick();
                    for (int client = 0; client < testWorld.ClientWorlds.Length; ++client)
                    {
                        var singletonEntity = testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[client]);
                        var netStats = testWorld.ClientWorlds[client].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(singletonEntity).MainStatsWrite;
                        // 收集首次加入时的统计数据供后续比较
                        if (netStats.PerGhostTypeStatsListRefRW.Length == 2)
                        {
                            firstTimeJoinStats[3 * client] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount; // 包中的实体数
                            firstTimeJoinStats[3 * client + 1] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits; // 接收的位数
                            firstTimeJoinStats[3 * client + 2] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount; // 未压缩实体数
                        }
                    }
                    if (firstTimeJoinStats[0] >= numObjects)
                        break;
                }
                // 依次让每个客户端退出并重新进入游戏
                // 验证重新加入后收到的数据符合预期
                for (int client = 0; client < numClients; ++client)
                {
                    rejoinTickCount = 0;
                    testWorld.RemoveFromGame(client);
                    UnloadSubScene(testWorld.ClientWorlds[client]);
                    // 推进若干 Tick 以重置所有内部数据结构
                    for (int k = 0; k < 6; ++k)
                        testWorld.Tick();
                    // 验证全部映射和确认列表均已清空
                    var singletonEntity = testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[client]);
                    var netStats = testWorld.ClientWorlds[client].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(singletonEntity).MainStatsWrite;
                    var recvGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[client]);
                    Assert.AreEqual(0, testWorld.ClientWorlds[client].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value.Count());
                    Assert.AreEqual(0, netStats.PerGhostTypeStatsListRefRW.Length);
                    var inGame = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>(),
                        ComponentType.Exclude<NetworkStreamInGame>()).ToEntityArray(Allocator.Temp);
                    Assert.AreEqual(1, inGame.Length);
                    Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.GetBuffer<PrespawnSectionAck>(inGame[0]).Length);
                    inGame.Dispose();
                    // 让客户端重新进入游戏并再次接收全部数据
                    SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[client]);
                    testWorld.SetInGame(client);
                    // 使用与首次加入相同的最大 Tick 数推进并收集重连数据
                    for(int k=0;k<32;++k)
                    {
                        ++rejoinTickCount;
                        testWorld.Tick();
                        singletonEntity = testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[client]);
                        netStats = testWorld.ClientWorlds[client].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(singletonEntity).MainStatsWrite;
                        if (netStats.PerGhostTypeStatsListRefRW.Length == 2)
                        {
                            rejoinStats[3 * client] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount; // 包中的实体数
                            rejoinStats[3 * client + 1] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits; // 接收的位数
                            rejoinStats[3 * client + 2] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount; // 未压缩实体数
                        }
                        if (rejoinStats[3 * client] >= numObjects)
                            break;
                    }
                    // 预生成 Ghost 延迟初始化会额外占用一个 Tick
                    // ClientSystemGroup 的已知问题使精确 Tick 数不稳定，因此允许多一个 Tick
                    Assert.IsTrue(rejoinTickCount>=firstJoinTickCount &&
                                  rejoinTickCount<firstJoinTickCount+2,
                                  "The number of ticks necessary to receive all the ghosts must be the same");
                    // 检查重新加入时收到的实体数与首次加入完全一致
                    Assert.AreEqual(rejoinStats[3 * client], firstTimeJoinStats[3 * client], "re-joining client must receive the same number of entities as the first time");
                    // Tick 编码会使接收数据量略有差异，因此允许 8 bit 即 1 byte 的余量
                    const int extraMargin = 8;
                    Assert.GreaterOrEqual(rejoinStats[3 * client + 1], firstTimeJoinStats[3 * client + 1]);
                    Assert.LessOrEqual(rejoinStats[3 * client + 1], firstTimeJoinStats[3 * client + 1] + extraMargin);
                }

                // 退出游戏并停止服务器和所有客户端的场景流式加载
                testWorld.ExitFromGame();
                UnloadSubScene(testWorld.ServerWorld);
                for (int i = 0; i < numClients; ++i)
                    UnloadSubScene(testWorld.ClientWorlds[i]);
                // 推进若干 Tick 以确保所有系统完成重置
                for (int k = 0; k < 4; ++k)
                    testWorld.Tick();
                // 检查服务器数据已经清理且客户端不再保留预生成数据
                for (int i = 0; i < 2; ++i)
                {
                    var singletonEntity = testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i]);
                    var netStats = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(singletonEntity).MainStatsWrite;
                    var recvGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[i]);
                    Assert.AreEqual(0, testWorld.ClientWorlds[i].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value.Count(), "client spawn map must be empty");
                    Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<SubScenePrespawnBaselineResolved>(testWorld.ClientWorlds[i]));
                    Assert.AreEqual(0, netStats.PerGhostTypeStatsListRefRW.Length, "client ghost stats must be empty");

                    var appliedPredictionTicks = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostPredictionGroupTickState>(testWorld.TryGetSingletonEntity<GhostPredictionGroupTickState>(testWorld.ClientWorlds[i])).AppliedPredictedTicks;
                    Assert.AreEqual(0, appliedPredictionTicks.Count(), "client prediction tick must be 0");
                }
                var sendGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ServerWorld);
                Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.GetComponentData<SpawnedGhostEntityMap>(sendGhostMapSingleton).Value.Count(), "server ghost map must be empty");
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<SubScenePrespawnBaselineResolved>(testWorld.ServerWorld));
                Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.GetComponentData<SpawnedGhostEntityMap>(sendGhostMapSingleton).ServerDestroyedPrespawns.Length, "server prespawn despawn list must be empty");
                var serverConnections = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>()).ToEntityArray(Allocator.Temp);
                Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.GetBuffer<PrespawnSectionAck>(serverConnections[0]).Length);
                Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.GetBuffer<PrespawnSectionAck>(serverConnections[1]).Length);
                // 再次进入游戏并检查所有对象重新到达且耗用 Tick 数一致
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.GoInGame();
                for (int i = 0; i < numClients; ++i)
                {
                    rejoinStats[3*i] = 0;
                    rejoinStats[3*i+1] = 0;
                    rejoinStats[3*i+2] = 0;
                }

                rejoinTickCount = 0;
                for (int i = 0; i < 16; ++i)
                {
                    ++rejoinTickCount;
                    testWorld.Tick();
                    for (int client = 0; client < testWorld.ClientWorlds.Length; ++client)
                    {
                        var singletonEntity = testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[client]);
                        var netStats = testWorld.ClientWorlds[client].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(singletonEntity).MainStatsWrite;
                        if (netStats.PerGhostTypeStatsListRefRW.Length == 2)
                        {
                            rejoinStats[3 * client] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount; // 包中的实体数
                            rejoinStats[3 * client + 1] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits; // 接收的位数
                            rejoinStats[3 * client + 2] += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount; // 未压缩实体数
                        }
                    }
                    if (rejoinStats[0] >= numObjects)
                        break;
                }
                // 预生成 Ghost 延迟初始化会额外占用一个 Tick
                Assert.IsTrue(rejoinTickCount>=firstJoinTickCount &&
                              rejoinTickCount<firstJoinTickCount+2,
                    "re-joining the server should take the same number of ticks");
                for (int client = 0; client < testWorld.ClientWorlds.Length; ++client)
                {
                    Assert.AreEqual(firstTimeJoinStats[3*client], rejoinStats[3*client], "client must receive the same number of ghosts");
                    Assert.AreEqual(firstTimeJoinStats[3*client+2], rejoinStats[3*client+2], "client must received the same number of uncompressed entities (0)");
                    // Tick 持续增长会使编码后的接收位数略有增加，因此允许一定余量
                    const int extraMargin = 8;
                    Assert.IsTrue(rejoinStats[3*client+1] >= firstTimeJoinStats[3*client+1] &&
                                  rejoinStats[3*client+1] <= firstTimeJoinStats[3*client+1] + extraMargin,
                        "client must receive ~same amount of bytes");
                }
            }
        }
    }
}
