using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    // 用于验证 Ghost ID 和 Spawn Tick 在迁移前后保持一致
    internal struct GhostIdAndTickChecker : IComponentData
    {
        [GhostField] public int originalGhostId;
        [GhostField] public NetworkTick originalSpawnTick;
    }

    // 用于标记迁移操作（保存或加载）后生成的 Ghost，便于测试定位
    internal struct CreatedPostHostMigrationAction : IComponentData
    { }

    internal class GhostIdAndTickCheckerAuthoring : MonoBehaviour
    {
    }

    class GhostIdAndTickCheckerAuthoringBaker : Baker<GhostIdAndTickCheckerAuthoring>
    {
        public override void Bake(GhostIdAndTickCheckerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new GhostIdAndTickChecker());
        }
    }
}
