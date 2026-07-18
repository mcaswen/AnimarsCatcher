using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
/// <summary>
/// 标记使用近战采集攻击表现的 Picker Ani
/// </summary>
[GhostComponent]
public struct PickerAniTag : IComponentData {}

/// <summary>
/// 标记使用远程射线攻击表现的 Blaster Ani
/// </summary>
[GhostComponent]
public struct BlasterAniTag : IComponentData {}

/// <summary>
/// 表示 Ani 当前已加入玩家编队，可在网络两端独立启停
/// </summary>
[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
[GhostEnabledBit]
public struct AniInTeamTag : IComponentData, IEnableableComponent {}

/// <summary>
/// 表示 Ani 当前被本地交互选中，可通过 Ghost 启用位同步
/// </summary>
[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
[GhostEnabledBit]
public struct AniSelectedTag : IComponentData, IEnableableComponent {}
}
