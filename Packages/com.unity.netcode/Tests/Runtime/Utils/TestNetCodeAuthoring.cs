using UnityEngine;
using Unity.Entities;

internal class TestNetCodeAuthoring : MonoBehaviour
{
    internal interface IConverter
    {
        void Bake(GameObject gameObject, IBaker baker);
    }
    public IConverter Converter;
}

class TestNetCodeAuthoringBaker : Baker<TestNetCodeAuthoring>
{
    public override void Bake(TestNetCodeAuthoring authoring)
    {
        if (authoring.Converter != null)
            authoring.Converter.Bake(authoring.gameObject, this);
    }
}
