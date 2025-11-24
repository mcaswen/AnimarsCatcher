using UnityEngine;

public class BeamDebug : MonoBehaviour
{
    private void Start()
    {
        var owner = GetComponentInParent<BlasterAniAttackView>();
        Debug.Log($"[BeamDebug] Beam instance={GetInstanceID()}, root={transform.root.name}, " +
                  $"ownerView={(owner ? owner.name : "none")}, ownerInstance={(owner ? owner.GetInstanceID() : 0)}");
    }
}