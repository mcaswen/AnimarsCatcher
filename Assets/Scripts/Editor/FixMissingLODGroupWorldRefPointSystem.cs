// using Unity.Burst;
// using Unity.Collections;
// using Unity.Entities;
// using Unity.Mathematics;
// using Unity.Rendering;

// // Editor 下的临时补丁系统：
// // 有 MeshLODGroupComponent 但缺 LODGroupWorldReferencePoint 的 LOD 组
// // 会自动被补上 LODGroupWorldReferencePoint，避免 LODRequirementsUpdateSystem 访问空组件时报错。
// [BurstCompile]
// public partial struct FixMissingLODGroupWorldRefPointSystem : ISystem
// {
//     private EntityQuery _missingGroupWorldRefQuery;

//     [BurstCompile]
//     public void OnCreate(ref SystemState state)
//     {
//         // 查询所有 LODGroup 实体：
//         //  - 有 MeshLODGroupComponent（代表这是一个 LODGroup）
//         //  - 没有 LODGroupWorldReferencePoint（缺关键组件）
//         _missingGroupWorldRefQuery = SystemAPI.QueryBuilder()
//             .WithAll<MeshLODGroupComponent>()          // LOD group 入口组件（官方公开 API）
//             .WithNone<LODGroupWorldReferencePoint>()   // 缺的那个组件
//             .Build(ref state);

//         // 只有真的存在这种 entity 时才更新
//         state.RequireForUpdate(_missingGroupWorldRefQuery);
//     }

//     [BurstCompile]
//     public void OnUpdate(ref SystemState state)
//     {
//         if (_missingGroupWorldRefQuery.IsEmptyIgnoreFilter)
//             return;

//         var entityManager = state.EntityManager;

//         using var groups = _missingGroupWorldRefQuery.ToEntityArray(Allocator.Temp);

//         foreach (var group in groups)
//         {
//             // 先随便给一个默认值，真正正确的 world reference point
//             // 会在 LODRequirementsUpdateSystem 里被立刻重算覆盖。
//             entityManager.AddComponentData(
//                 group,
//                 new LODGroupWorldReferencePoint
//                 {
//                     Value = float3.zero
//                 });
//         }
//     }

//     [BurstCompile]
//     public void OnDestroy(ref SystemState state)
//     {
//     }
// }