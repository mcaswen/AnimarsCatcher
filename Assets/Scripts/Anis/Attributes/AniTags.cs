using Unity.Entities;
using Unity.NetCode;

[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct PickerAniTag : IComponentData {}


[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct BlasterAniTag : IComponentData {}


[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
[GhostEnabledBit]
public struct AniInTeamTag : IComponentData, IEnableableComponent {}


[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
[GhostEnabledBit]
public struct AniSelectedTag : IComponentData, IEnableableComponent {}