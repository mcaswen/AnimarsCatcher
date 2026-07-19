using UnityEngine;

/// <summary>
/// 配置旧 NavMesh 移动状态机性能基线的初始状态
/// </summary>
[DisallowMultipleComponent]
public class AniMovementFsmAuthoring : MonoBehaviour
{
    [Tooltip("仅预分配容量，小于 4 时按 4 处理")]
    public int initialBlackboardCapacity = 32;

    [Tooltip("当前仅支持 IdleStateId")]
    public ushort initialState = AniMovementFsmIds.IdleStateId;
}
