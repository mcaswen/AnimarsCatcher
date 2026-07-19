namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using Unity.Mathematics;
    using UnityEngine;
    using Unity.CharacterController;
    using Unity.NetCode;

    /// <summary>
    /// 标记参与角色控制流程的实体
    /// </summary>
    public class CharacterTag : IComponentData { }


    /// <summary>
    /// 配置第三人称 KCC 角色的移动、跳跃和斜坡参数
    /// </summary>
    [DisallowMultipleComponent]
    public class ThirdPersonCharacterAuthoring : MonoBehaviour
    {
        public AuthoringKinematicCharacterProperties CharacterProperties = AuthoringKinematicCharacterProperties.GetDefault();

        public float RotationSharpness = 25f;
        public float GroundMaxSpeed = 10f;
        public float GroundedMovementSharpness = 15f;
        public float AirAcceleration = 50f;
        public float AirMaxSpeed = 10f;
        public float AirDrag = 0f;
        public float JumpSpeed = 10f;
        public float3 Gravity = math.up() * - 30f;
        public bool PreventAirAccelerationAgainstUngroundedHits = true;
        public BasicStepAndSlopeHandlingParameters StepAndSlopeHandling = BasicStepAndSlopeHandlingParameters.GetDefault();

        /// <summary>
        /// 负责创建第三人称 KCC 角色的预测组件
        /// </summary>
        public class Baker : Baker<ThirdPersonCharacterAuthoring>
        {
            /// <summary>
            /// 烘焙 KCC 配置、控制组件和网络输入缓冲
            /// </summary>
            /// <param name="authoring">第三人称角色 Authoring 配置</param>
            public override void Bake(ThirdPersonCharacterAuthoring authoring)
            {
                KinematicCharacterUtilities.BakeCharacter(this, authoring.gameObject, authoring.CharacterProperties);

                Entity entity = GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.WorldSpace);

                AddComponent(entity, new ThirdPersonCharacter
                {
                    RotationSharpness = authoring.RotationSharpness,
                    GroundMaxSpeed = authoring.GroundMaxSpeed,
                    GroundedMovementSharpness = authoring.GroundedMovementSharpness,
                    AirAcceleration = authoring.AirAcceleration,
                    AirMaxSpeed = authoring.AirMaxSpeed,
                    AirDrag = authoring.AirDrag,
                    Gravity = authoring.Gravity,
                    PreventAirAccelerationAgainstUngroundedHits = authoring.PreventAirAccelerationAgainstUngroundedHits,
                    StepAndSlopeHandling = authoring.StepAndSlopeHandling,
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
