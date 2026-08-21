namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using Unity.Mathematics;
    using UnityEngine;
    using UnityEngine.Serialization;
    using Unity.CharacterController;
    using Unity.NetCode;

    /// <summary>
    /// 标记参与角色控制流程的 Entity
    /// </summary>
    public struct CharacterTag : IComponentData { }


    /// <summary>
    /// 配置第三人称 KCC 角色的移动、跳跃和斜坡参数
    /// </summary>
    [DisallowMultipleComponent]
    public class ThirdPersonCharacterAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("CharacterProperties")]
        [SerializeField] private AuthoringKinematicCharacterProperties _characterProperties = AuthoringKinematicCharacterProperties.GetDefault();

        [FormerlySerializedAs("RotationSharpness")]
        [SerializeField] private float _rotationSharpness = 25f;
        [FormerlySerializedAs("GroundMaxSpeed")]
        [SerializeField] private float _groundMaximumSpeed = 10f;
        [FormerlySerializedAs("GroundedMovementSharpness")]
        [SerializeField] private float _groundedMovementSharpness = 15f;
        [FormerlySerializedAs("AirAcceleration")]
        [SerializeField] private float _airAcceleration = 50f;
        [FormerlySerializedAs("AirMaxSpeed")]
        [SerializeField] private float _airMaximumSpeed = 10f;
        [FormerlySerializedAs("AirDrag")]
        [SerializeField] private float _airDrag = 0f;
        [FormerlySerializedAs("Gravity")]
        [SerializeField] private float3 _gravity = math.up() * - 30f;
        [FormerlySerializedAs("PreventAirAccelerationAgainstUngroundedHits")]
        [SerializeField] private bool _preventAirAccelerationAgainstUngroundedHits = true;
        [FormerlySerializedAs("StepAndSlopeHandling")]
        [SerializeField] private BasicStepAndSlopeHandlingParameters _stepAndSlopeHandling = BasicStepAndSlopeHandlingParameters.GetDefault();

        private sealed class Baker : Baker<ThirdPersonCharacterAuthoring>
        {
            public override void Bake(ThirdPersonCharacterAuthoring authoring)
            {
                KinematicCharacterUtilities.BakeCharacter(this, authoring.gameObject, authoring._characterProperties);

                Entity entity = GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.WorldSpace);

                AddComponent(entity, new ThirdPersonCharacter
                {
                    RotationSharpness = authoring._rotationSharpness,
                    GroundMaxSpeed = authoring._groundMaximumSpeed,
                    GroundedMovementSharpness = authoring._groundedMovementSharpness,
                    AirAcceleration = authoring._airAcceleration,
                    AirMaxSpeed = authoring._airMaximumSpeed,
                    AirDrag = authoring._airDrag,
                    Gravity = authoring._gravity,
                    PreventAirAccelerationAgainstUngroundedHits = authoring._preventAirAccelerationAgainstUngroundedHits,
                    StepAndSlopeHandling = authoring._stepAndSlopeHandling,
                });

                AddComponent(entity, new ThirdPersonCharacterControl());

                AddComponent<PredictedGhost>(entity);
                AddComponent<CharacterTag>(entity);
                AddComponent<Simulate>(entity);

                AddBuffer<InputCommand>(entity);
            }
        }
    }
}
