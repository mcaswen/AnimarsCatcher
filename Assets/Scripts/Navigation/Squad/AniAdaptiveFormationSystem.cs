using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 根据 Anchor 前方的静态 Clearance 和动态 Overlay 计算阵型列数
    /// 收缩立即生效，展开需要连续多个 Tick 满足宽度条件
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniSquadAnchorAdvanceSystem))]
    [UpdateBefore(typeof(AniFormationLayoutSystem))]
    public partial struct AniAdaptiveFormationSystem : ISystem
    {
        private const float HorizontalGap = 0.4f;
        private const float BoundaryMargin = 0.2f;
        private const float ExpectedReformTime = 0.75f;
        private const int ExpansionStableTicks = 8;

        private EntityQuery _gridQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _gridQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_gridQuery.CalculateEntityCount() != 1)
            {
                return;
            }

            Entity gridEntity = _gridQuery.GetSingletonEntity();
            EntityManager entityManager = state.EntityManager;
            NavigationGridReference gridReference = entityManager.GetComponentData<
                NavigationGridReference>(gridEntity);
            if (!gridReference.Value.IsCreated)
            {
                return;
            }

            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            bool hasOverlay = entityManager.HasBuffer<NavigationDynamicOverlayCell>(gridEntity);
            DynamicBuffer<NavigationDynamicOverlayCell> overlay = hasOverlay
                ? entityManager.GetBuffer<NavigationDynamicOverlayCell>(
                    gridEntity,
                    isReadOnly: true)
                : default;
            uint overlayVersion = entityManager.HasComponent<NavigationDynamicOverlayState>(gridEntity)
                ? entityManager.GetComponentData<NavigationDynamicOverlayState>(gridEntity).Version
                : 1u;

            foreach (var (squad, anchor, formation, members) in SystemAPI.Query<
                         RefRO<AniSquad>,
                         RefRO<AniSquadAnchor>,
                         RefRW<AniSquadFormationState>,
                         DynamicBuffer<AniSquadMember>>())
            {
                if (members.IsEmpty)
                {
                    continue;
                }

                float maximumAgentRadius = math.max(0f, squad.ValueRO.MaximumAgentRadius);
                int clearSamples = 0;
                float minimumClearance = float.PositiveInfinity;
                float3 forward = math.normalizesafe(
                    anchor.ValueRO.Forward,
                    new float3(0f, 0f, 1f));
                float speed = math.length(new float2(
                    anchor.ValueRO.Velocity.x,
                    anchor.ValueRO.Velocity.z));
                int currentColumns = math.clamp(
                    formation.ValueRO.ColumnCount,
                    1,
                    members.Length);
                int rowCount = math.max(1, (members.Length + currentColumns - 1) / currentColumns);
                float formationDepth = rowCount * 1.6f;
                float lookDistance = math.max(
                    grid.CellSize,
                    speed * ExpectedReformTime + formationDepth);
                int sampleCount = math.max(1, (int)math.ceil(lookDistance / grid.CellSize));

                for (int sampleIndex = 0; sampleIndex <= sampleCount; sampleIndex++)
                {
                    float3 samplePosition = anchor.ValueRO.Position +
                                             forward * (sampleIndex * grid.CellSize);
                    if (!NavigationGridQuery.TryWorldToCell(
                            ref grid,
                            samplePosition,
                            out _,
                            out int cellIndex))
                    {
                        break;
                    }

                    NavigationGridCell staticCell = grid.Cells[cellIndex];
                    float clearance = hasOverlay
                        ? NavigationDynamicOverlayAlgorithms.GetEffectiveClearance(
                            ref staticCell,
                            overlay,
                            cellIndex)
                        : staticCell.Clearance;
                    if (hasOverlay &&
                        NavigationDynamicOverlayAlgorithms.IsBlocked(overlay, cellIndex))
                    {
                        break;
                    }

                    minimumClearance = math.min(minimumClearance, clearance);
                    if (clearance + 0.00001f <
                        NavigationGridCost.CalculateRequiredClearance(
                            ref grid,
                            squad.ValueRO.MaximumAgentRadius,
                            BoundaryMargin))
                    {
                        break;
                    }

                    clearSamples++;
                }

                if (clearSamples == 0)
                {
                    minimumClearance = 0f;
                }

                float usableWidth = math.max(
                    0f,
                    2f * minimumClearance - 2f * BoundaryMargin);
                int desiredColumns = AniSquadFormationAlgorithms.CalculateAdaptiveColumnCount(
                    formation.ValueRO.Kind,
                    members.Length,
                    usableWidth,
                    maximumAgentRadius * 2f,
                    HorizontalGap);

                int activeColumns = currentColumns;
                int stableTicks = formation.ValueRO.WidthStableTicks;
                bool layoutChanged = false;
                if (desiredColumns < activeColumns)
                {
                    activeColumns = desiredColumns;
                    stableTicks = 0;
                    layoutChanged = true;
                }
                else if (desiredColumns > activeColumns)
                {
                    stableTicks = formation.ValueRO.DesiredColumnCount == desiredColumns
                        ? math.min(ExpansionStableTicks, stableTicks + 1)
                        : 1;
                    if (stableTicks >= ExpansionStableTicks)
                    {
                        activeColumns = desiredColumns;
                        stableTicks = 0;
                        layoutChanged = true;
                    }
                }
                else
                {
                    stableTicks = 0;
                }

                formation.ValueRW.ForwardClearance = clearSamples * grid.CellSize;
                formation.ValueRW.DesiredColumnCount = desiredColumns;
                formation.ValueRW.WidthStableTicks = stableTicks;
                formation.ValueRW.NarrowPath = desiredColumns < currentColumns ? (byte)1 : (byte)0;
                formation.ValueRW.ClearanceVersion = overlayVersion;
                if (layoutChanged)
                {
                    formation.ValueRW.ColumnCount = activeColumns;
                    formation.ValueRW.LayoutVersion = 0;
                    formation.ValueRW.AssignmentVersion = 0;
                }
            }
        }
    }
}
