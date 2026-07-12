#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.CharacterController;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Authoring;
using Unity.Physics.Extensions;
using Unity.Physics.Systems;
using Unity.Transforms;
using UnityEngine;

/// <summary>保存一次 KCC 更新期间需要跨回调共享的上下文</summary>
public struct ThirdPersonCharacterUpdateContext
{
    // Lookup、单例或原生容器应集中放在此处，供同一帧的角色回调复用

    public uint DebugTick;

    /// <summary>初始化角色更新上下文中的长期数据</summary>
    /// <param name="state">创建上下文的系统状态</param>
    public void OnSystemCreate(ref SystemState state)
    {
        // 当前上下文没有需要长期缓存的 Lookup
    }

    /// <summary>刷新角色更新上下文中的逐帧数据</summary>
    /// <param name="state">执行更新的系统状态</param>
    public void OnSystemUpdate(ref SystemState state)
    {
        // 当前上下文没有需要逐帧刷新的 Lookup
    }
}

/// <summary>实现第三人称角色的 KCC 物理、速度和碰撞处理策略</summary>
public readonly partial struct ThirdPersonCharacterAspect : IAspect, IKinematicCharacterProcessor<ThirdPersonCharacterUpdateContext>
{
    public readonly KinematicCharacterAspect CharacterAspect;
    public readonly RefRW<ThirdPersonCharacter> CharacterComponent;
    public readonly RefRW<ThirdPersonCharacterControl> CharacterControl;

    /// <summary>按 KCC 固定物理阶段的顺序更新角色接地、速度和碰撞</summary>
    /// <param name="context">第三人称角色更新上下文</param>
    /// <param name="baseContext">KCC 基础更新上下文</param>
    public void PhysicsUpdate(ref ThirdPersonCharacterUpdateContext context, ref KinematicCharacterUpdateContext baseContext)
    {
        ref ThirdPersonCharacter characterComponent = ref CharacterComponent.ValueRW;
        ref KinematicCharacterBody characterBody = ref CharacterAspect.CharacterBody.ValueRW;
        ref float3 characterPosition = ref CharacterAspect.LocalTransform.ValueRW.Position;

        // 第一阶段先处理父实体位移和接地，以便后续速度计算使用当前地面状态
        CharacterAspect.Update_Initialize(in this, ref context, ref baseContext, ref characterBody, baseContext.Time.DeltaTime);
        CharacterAspect.Update_ParentMovement(in this, ref context, ref baseContext, ref characterBody, ref characterPosition, characterBody.WasGroundedBeforeCharacterUpdate);
        CharacterAspect.Update_Grounding(in this, ref context, ref baseContext, ref characterBody, ref characterPosition);

        // 接地完成后更新期望速度，后续推挤和碰撞阶段依赖该结果
        HandleVelocityControl(ref context, ref baseContext);

        // 第二阶段处理斜坡限制、地面推挤、位移解穿透和移动平台动量
        CharacterAspect.Update_PreventGroundingFromFutureSlopeChange(in this, ref context, ref baseContext, ref characterBody, in characterComponent.StepAndSlopeHandling);
        CharacterAspect.Update_GroundPushing(in this, ref context, ref baseContext, characterComponent.Gravity);
        CharacterAspect.Update_MovementAndDecollisions(in this, ref context, ref baseContext, ref characterBody, ref characterPosition);
        CharacterAspect.Update_MovingPlatformDetection(ref baseContext, ref characterBody);
        CharacterAspect.Update_ParentMomentum(ref baseContext, ref characterBody);
        CharacterAspect.Update_ProcessStatefulCharacterHits();
    }

    private void HandleVelocityControl(ref ThirdPersonCharacterUpdateContext context, ref KinematicCharacterUpdateContext baseContext)
    {
        float deltaTime = baseContext.Time.DeltaTime;
        ref KinematicCharacterBody characterBody = ref CharacterAspect.CharacterBody.ValueRW;
        ref ThirdPersonCharacter characterComponent = ref CharacterComponent.ValueRW;
        ref ThirdPersonCharacterControl characterControl = ref CharacterControl.ValueRW;

        // 站在旋转父实体上时同步旋转输入和相对速度
        if (characterBody.ParentEntity != Entity.Null)
        {
            characterControl.MoveVector = math.rotate(characterBody.RotationFromParent, characterControl.MoveVector);
            characterBody.RelativeVelocity = math.rotate(characterBody.RotationFromParent, characterBody.RelativeVelocity);

        }

        if (characterBody.IsGrounded)
        {
            // 接地时将速度平滑收敛到地面切线上的目标速度
            float3 targetVelocity = characterControl.MoveVector * characterComponent.GroundMaxSpeed;
            CharacterControlUtilities.StandardGroundMove_Interpolated(ref characterBody.RelativeVelocity, targetVelocity, characterComponent.GroundedMovementSharpness, deltaTime, characterBody.GroundingUp, characterBody.GroundHit.Normal);
        }
        else
        {
            // 空中移动只施加加速度，并受最大空速限制
            float3 airAcceleration = characterControl.MoveVector * characterComponent.AirAcceleration;
            if (math.lengthsq(airAcceleration) > 0f)
            {
                float3 tmpVelocity = characterBody.RelativeVelocity;
                CharacterControlUtilities.StandardAirMove(ref characterBody.RelativeVelocity, airAcceleration, characterComponent.AirMaxSpeed, characterBody.GroundingUp, deltaTime, false);

                // 若输入加速度会撞上非地面表面则回退，避免高加速度沿陡坡爬升
                if (characterComponent.PreventAirAccelerationAgainstUngroundedHits && CharacterAspect.MovementWouldHitNonGroundedObstruction(in this, ref context, ref baseContext, characterBody.RelativeVelocity * deltaTime, out ColliderCastHit hit))
                {
                    characterBody.RelativeVelocity = tmpVelocity;
                }
            }

            // 重力和阻力只在离地阶段作用于相对速度
            CharacterControlUtilities.AccelerateVelocity(ref characterBody.RelativeVelocity, characterComponent.Gravity, deltaTime);

            CharacterControlUtilities.ApplyDragToVelocity(ref characterBody.RelativeVelocity, deltaTime, characterComponent.AirDrag);
        }
    }

    /// <summary>在可变帧率阶段更新父实体旋转插值和角色朝向</summary>
    /// <param name="context">第三人称角色更新上下文</param>
    /// <param name="baseContext">KCC 基础更新上下文</param>
    public void VariableUpdate(ref ThirdPersonCharacterUpdateContext context, ref KinematicCharacterUpdateContext baseContext)
    {
        ref KinematicCharacterBody characterBody = ref CharacterAspect.CharacterBody.ValueRW;
        ref ThirdPersonCharacter characterComponent = ref CharacterComponent.ValueRW;
        ref ThirdPersonCharacterControl characterControl = ref CharacterControl.ValueRW;
        ref quaternion characterRotation = ref CharacterAspect.LocalTransform.ValueRW.Rotation;

        // 以可变帧率插值父实体旋转，使旋转平台上的角色表现连续
        KinematicCharacterUtilities.AddVariableRateRotationFromFixedRateRotation(ref characterRotation, characterBody.RotationFromParent, baseContext.Time.DeltaTime, characterBody.LastPhysicsUpdateDeltaTime);

        // 有移动输入时平滑转向移动方向
        if (math.lengthsq(characterControl.MoveVector) > 0f)
        {
            CharacterControlUtilities.SlerpRotationTowardsDirectionAroundUp(ref characterRotation, baseContext.Time.DeltaTime, math.normalizesafe(characterControl.MoveVector), characterBody.GroundingUp, characterComponent.RotationSharpness);
        }
    }

    #region KCC 角色处理回调
    /// <summary>更新角色用于接地判定的上方向</summary>
    /// <param name="context">第三人称角色更新上下文</param>
    /// <param name="baseContext">KCC 基础更新上下文</param>
    public void UpdateGroundingUp(
        ref ThirdPersonCharacterUpdateContext context,
        ref KinematicCharacterUpdateContext baseContext)
    {
        ref KinematicCharacterBody characterBody = ref CharacterAspect.CharacterBody.ValueRW;

        CharacterAspect.Default_UpdateGroundingUp(ref characterBody);
    }

    /// <summary>判断命中材质是否允许参与角色碰撞</summary>
    /// <param name="context">第三人称角色更新上下文</param>
    /// <param name="baseContext">KCC 基础更新上下文</param>
    /// <param name="hit">基础碰撞命中</param>
    /// <returns>命中是否可碰撞</returns>
    public bool CanCollideWithHit(
        ref ThirdPersonCharacterUpdateContext context,
        ref KinematicCharacterUpdateContext baseContext,
        in BasicHit hit)
    {
        return PhysicsUtilities.IsCollidable(hit.Material);
    }

    /// <summary>按斜坡和台阶配置判断命中是否可作为地面</summary>
    /// <param name="context">第三人称角色更新上下文</param>
    /// <param name="baseContext">KCC 基础更新上下文</param>
    /// <param name="hit">基础碰撞命中</param>
    /// <param name="groundingEvaluationType">接地评估类型</param>
    /// <returns>命中是否满足接地条件</returns>
    public bool IsGroundedOnHit(
        ref ThirdPersonCharacterUpdateContext context,
        ref KinematicCharacterUpdateContext baseContext,
        in BasicHit hit,
        int groundingEvaluationType)
    {
        ThirdPersonCharacter characterComponent = CharacterComponent.ValueRO;

        return CharacterAspect.Default_IsGroundedOnHit(
            in this,
            ref context,
            ref baseContext,
            in hit,
            in characterComponent.StepAndSlopeHandling,
            groundingEvaluationType);
    }

    /// <summary>使用默认 KCC 规则处理移动碰撞和台阶跨越</summary>
    /// <param name="context">第三人称角色更新上下文</param>
    /// <param name="baseContext">KCC 基础更新上下文</param>
    /// <param name="hit">本次角色移动命中</param>
    /// <param name="remainingMovementDirection">剩余移动方向</param>
    /// <param name="remainingMovementLength">剩余移动距离</param>
    /// <param name="originalVelocityDirection">原始速度方向</param>
    /// <param name="hitDistance">命中距离</param>
    public void OnMovementHit(
            ref ThirdPersonCharacterUpdateContext context,
            ref KinematicCharacterUpdateContext baseContext,
            ref KinematicCharacterHit hit,
            ref float3 remainingMovementDirection,
            ref float remainingMovementLength,
            float3 originalVelocityDirection,
            float hitDistance)
    {
        ref KinematicCharacterBody characterBody = ref CharacterAspect.CharacterBody.ValueRW;
        ref float3 characterPosition = ref CharacterAspect.LocalTransform.ValueRW.Position;
        ThirdPersonCharacter characterComponent = CharacterComponent.ValueRO;

        CharacterAspect.Default_OnMovementHit(
            in this,
            ref context,
            ref baseContext,
            ref characterBody,
            ref characterPosition,
            ref hit,
            ref remainingMovementDirection,
            ref remainingMovementLength,
            originalVelocityDirection,
            hitDistance,
            characterComponent.StepAndSlopeHandling.StepHandling,
            characterComponent.StepAndSlopeHandling.MaxStepHeight,
            characterComponent.StepAndSlopeHandling.CharacterWidthForStepGroundingCheck);
    }

    /// <summary>保留 KCC 默认动态碰撞质量</summary>
    /// <param name="context">第三人称角色更新上下文</param>
    /// <param name="baseContext">KCC 基础更新上下文</param>
    /// <param name="characterMass">角色质量</param>
    /// <param name="otherMass">被命中物体质量</param>
    /// <param name="hit">基础碰撞命中</param>
    public void OverrideDynamicHitMasses(
        ref ThirdPersonCharacterUpdateContext context,
        ref KinematicCharacterUpdateContext baseContext,
        ref PhysicsMass characterMass,
        ref PhysicsMass otherMass,
        BasicHit hit)
    {
        // 当前玩法不覆盖质量，保留接口以明确采用 KCC 默认行为
    }

    /// <summary>按命中法线和地面约束投影角色速度</summary>
    /// <param name="context">第三人称角色更新上下文</param>
    /// <param name="baseContext">KCC 基础更新上下文</param>
    /// <param name="velocity">待投影速度</param>
    /// <param name="characterIsGrounded">角色接地状态</param>
    /// <param name="characterGroundHit">当前地面命中</param>
    /// <param name="velocityProjectionHits">速度投影命中集合</param>
    /// <param name="originalVelocityDirection">原始速度方向</param>
    public void ProjectVelocityOnHits(
        ref ThirdPersonCharacterUpdateContext context,
        ref KinematicCharacterUpdateContext baseContext,
        ref float3 velocity,
        ref bool characterIsGrounded,
        ref BasicHit characterGroundHit,
        in DynamicBuffer<KinematicVelocityProjectionHit> velocityProjectionHits,
        float3 originalVelocityDirection)
    {
        ThirdPersonCharacter characterComponent = CharacterComponent.ValueRO;

        CharacterAspect.Default_ProjectVelocityOnHits(
            ref velocity,
            ref characterIsGrounded,
            ref characterGroundHit,
            in velocityProjectionHits,
            originalVelocityDirection,
            characterComponent.StepAndSlopeHandling.ConstrainVelocityToGroundPlane);
    }
    #endregion
}
