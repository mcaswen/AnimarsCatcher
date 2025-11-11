using UnityEngine;

[DisallowMultipleComponent]
public class BlasterAniFsmAuthoring : MonoBehaviour
{
    [Tooltip("黑板初始容量")]
    public int initialBlackboardCapacity = 32;

    [Tooltip("初始状态")]
    public ushort initialState = BlasterAniFsmIDs.S_Idle;
}