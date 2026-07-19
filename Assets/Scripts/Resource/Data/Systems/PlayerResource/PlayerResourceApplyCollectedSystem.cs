using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 将资源事件 Hub 中的增量应用到对应玩家资源 Ghost
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PlayerResourceApplyCollectedSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var resourceQuery = SystemAPI.QueryBuilder()
                .WithAll<PlayerResourceTag, PlayerResourceState, GhostOwner>()
                .Build();

            var resourceEntities = resourceQuery.ToEntityArray(Allocator.Temp);
            var resourceStates = resourceQuery.ToComponentDataArray<PlayerResourceState>(Allocator.Temp);
            var owners = resourceQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);

            // 建立 NetworkId 到快照索引的映射 避免每条事件扫描全部玩家
            var idToIndex = new NativeHashMap<int, int>(resourceEntities.Length, Allocator.Temp);
            for (int i = 0; i < resourceEntities.Length; i++)
            {
                idToIndex.TryAdd(owners[i].NetworkId, i);
            }

            // 消费食物增量并在处理后清空缓冲区
            foreach (var buffer in SystemAPI.Query<DynamicBuffer<FoodAmountChangedEvent>>())
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    var eventData = buffer[i];
                    if (!idToIndex.TryGetValue(eventData.OwnerNetworkId, out var idx))
                        continue;

                    var resourceState = resourceStates[idx];
                    resourceState.FoodSum += eventData.Amount;
                    resourceStates[idx] = resourceState;
                }

                buffer.Clear();
            }

            // 消费水晶增量并在处理后清空缓冲区
            foreach (var buffer in SystemAPI.Query<DynamicBuffer<CrystalAmountChangedEvent>>())
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    var eventData = buffer[i];
                    if (!idToIndex.TryGetValue(eventData.OwnerNetworkId, out var idx))
                        continue;

                    var resourceState = resourceStates[idx];
                    resourceState.CrystalSum += eventData.Amount;
                    resourceStates[idx] = resourceState;
                }

                buffer.Clear();
            }

            for (int i = 0; i < resourceEntities.Length; i++)
            {
                state.EntityManager.SetComponentData(resourceEntities[i], resourceStates[i]);
            }

            resourceEntities.Dispose();
            resourceStates.Dispose();
            owners.Dispose();
            idToIndex.Dispose();
        }
    }
}
