using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 根据队伍前方的可通行宽度调整阵型：遇到窄路立即收拢，空间持续足够时再逐步展开
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniSquadAnchorAdvanceSystem))]
    [UpdateBefore(typeof(AniFormationLayoutSystem))]
    public partial struct AniAdaptiveFormationSystem : ISystem
    {
        private const float HorizontalGap = 0.4f;
        // Clearance 两侧扣除边界余量，避免单位贴着不可走区域移动
        private const float BoundaryMargin = 0.2f;
        // 根据当前速度估算阵型完成收拢所需的前视时间
        private const float ExpectedReformTime = 0.75f;
        // 展开必须连续观察到足够宽度，防止临界通道反复抖动
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

            // Overlay 可以缺失，缺失时只使用烘焙的静态 Clearance
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

            foreach (var (squad, command, anchor, pathState, formation, members) in SystemAPI.Query<
                         RefRO<AniSquad>,
                         RefRO<AniSquadCommand>,
                         RefRO<AniSquadAnchor>,
                         RefRO<AniSquadPathState>,
                         RefRW<AniSquadFormationState>,
                         DynamicBuffer<AniSquadMember>>())
            {
                if (members.IsEmpty)
                {
                    continue;
                }

                AniSquadMovementStatus movementStatus = pathState.ValueRO.Status;
                if (movementStatus == AniSquadMovementStatus.Failed ||
                    movementStatus == AniSquadMovementStatus.Completed ||
                    movementStatus == AniSquadMovementStatus.Holding)
                {
                    // 指令结束后保持最终阵型不动；收到新指令或重新寻路时才恢复宽度调整
                    continue;
                }

                float targetDistance = math.length(
                    PlanarMath.FlattenY(
                        pathState.ValueRO.ResolvedTargetPosition - anchor.ValueRO.Position));
                if (targetDistance <= math.max(0.1f, command.ValueRO.TargetStoppingDistance))
                {
                    // 锚点已经进入停止范围时，前方宽度已不能代表下一段路线，不再据此改变阵型
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
                int configuredColumns = math.clamp(
                    formation.ValueRO.ConfiguredColumnCount > 0
                        ? formation.ValueRO.ConfiguredColumnCount
                        : currentColumns,
                    1,
                    members.Length);
                // 阵型纵深影响需要提前观察多远的通道
                int rowCount = math.max(1, (members.Length + currentColumns - 1) / currentColumns);
                float formationDepth = rowCount * 1.6f;
                float lookDistance = math.min(
                    targetDistance,
                    math.max(grid.CellSize, speed * ExpectedReformTime + formationDepth));
                // 采样按 Grid CellSize 前进，包含锚点所在 Cell
                int sampleCount = math.max(
                    0,
                    (int)math.floor(lookDistance / grid.CellSize));

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
                        // 路线离开 Grid 边界后不再假设外部空间可通行
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
                        // 当前 Cell 连单列都无法通过，后续样本不再连续有效
                        break;
                    }

                    clearSamples++;
                }

                if (clearSamples == 0)
                {
                    minimumClearance = 0f;
                }

                // Clearance 是到边界的半径，需要乘二并扣除两侧余量
                float usableWidth = math.max(
                    0f,
                    2f * minimumClearance - 2f * BoundaryMargin);
                int desiredColumns = AniSquadFormationAlgorithms.CalculateAdaptiveColumnCount(
                    formation.ValueRO.Kind,
                    members.Length,
                    usableWidth,
                    maximumAgentRadius * 2f,
                    HorizontalGap);
                // 自适应逻辑只能收窄到配置宽度，不能自行扩得更宽
                desiredColumns = math.min(desiredColumns, configuredColumns);

                // 收拢立即生效，展开则通过稳定 Tick 形成滞回
                int activeColumns = currentColumns;
                int stableTicks = formation.ValueRO.WidthStableTicks;
                bool layoutChanged = false;
                if (desiredColumns < activeColumns)
                {
                    // 窄路优先保证可通行性，不等待稳定窗口
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
                        // 连续宽度足够后才扩大列数
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
                    // 列数变化会使布局和成员槽位分配全部失效
                    formation.ValueRW.ColumnCount = activeColumns;
                    formation.ValueRW.LayoutVersion = 0;
                    formation.ValueRW.AssignmentVersion = 0;
                }
            }
        }
    }
}
