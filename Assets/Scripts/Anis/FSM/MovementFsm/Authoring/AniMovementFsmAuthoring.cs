using UnityEngine;

[DisallowMultipleComponent]
public class AniMovementFsmAuthoring : MonoBehaviour
{
    [Tooltip("黑板初始容量")]
    public int initialBlackboardCapacity = 32;

    [Tooltip("初始状态")]
    public ushort initialState = AniMovementFsmIDs.S_Idle;
}
