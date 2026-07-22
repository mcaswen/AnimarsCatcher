using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Entities;

internal class NetcodeTransformUsageFlagsTestAuthoring : MonoBehaviour
{
    internal class Baker : Baker<NetcodeTransformUsageFlagsTestAuthoring>
    {
        public override void Bake(NetcodeTransformUsageFlagsTestAuthoring authoring)
        {
            AddTransformUsageFlags(TransformUsageFlags.Dynamic);
        }
    }
}
