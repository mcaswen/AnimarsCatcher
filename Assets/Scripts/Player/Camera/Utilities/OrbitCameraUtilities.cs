namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using Unity.CharacterController;
    using UnityEngine;

    /// <summary>
    /// 提供环绕相机目标姿态和位置的通用计算
    /// </summary>
    public static class OrbitCameraUtilities
    {
        /// <summary>
        /// 读取模拟阶段使用的相机目标世界变换
        /// </summary>
        /// <param name="targetCharacterEntity">受控角色实体</param>
        /// <param name="localTransformLookup">局部变换查询</param>
        /// <param name="parentLookup">父实体查询</param>
        /// <param name="postTransformMatrixLookup">后置变换查询</param>
        /// <param name="CameraTargetLookup">相机目标查询</param>
        /// <param name="worldTransform">输出目标世界变换</param>
        /// <returns>是否找到有效目标变换</returns>
        public static bool TryGetCameraTargetSimulationWorldTransform(
            Entity targetCharacterEntity,
            ref ComponentLookup<LocalTransform> localTransformLookup,
            ref ComponentLookup<Parent> parentLookup,
            ref ComponentLookup<PostTransformMatrix> postTransformMatrixLookup,
            ref ComponentLookup<CameraTarget> CameraTargetLookup,
            out float4x4 worldTransform)
        {
            bool foundValidCameraTarget = false;
            worldTransform = float4x4.identity;

            // 优先使用显式 CameraTarget，缺失时回退到角色自身变换
            if (CameraTargetLookup.TryGetComponent(targetCharacterEntity, out CameraTarget CameraTarget) &&
                localTransformLookup.HasComponent(CameraTarget.TargetEntity))
            {
                TransformHelpers.ComputeWorldTransformMatrix(
                    CameraTarget.TargetEntity,
                    out worldTransform,
                    ref localTransformLookup,
                    ref parentLookup,
                    ref postTransformMatrixLookup);
                foundValidCameraTarget = true;
            }
            else if (localTransformLookup.TryGetComponent(targetCharacterEntity, out LocalTransform characterLocalTransform))
            {
                worldTransform = float4x4.TRS(characterLocalTransform.Position, characterLocalTransform.Rotation, 1f);
                foundValidCameraTarget = true;
            }

            return foundValidCameraTarget;
        }

        /// <summary>
        /// 读取表现阶段使用的插值后相机目标世界变换
        /// </summary>
        /// <param name="targetCharacterEntity">受控角色实体</param>
        /// <param name="localToWorldLookup">插值后的世界变换查询</param>
        /// <param name="CameraTargetLookup">相机目标查询</param>
        /// <param name="worldTransform">输出目标世界变换</param>
        /// <returns>是否找到有效目标变换</returns>
        public static bool TryGetCameraTargetInterpolatedWorldTransform(
            Entity targetCharacterEntity,
            ref ComponentLookup<LocalToWorld> localToWorldLookup,
            ref ComponentLookup<CameraTarget> CameraTargetLookup,
            out LocalToWorld worldTransform)
        {
            bool foundValidCameraTarget = false;
            worldTransform = default;

            // 优先读取显式相机目标的插值姿态，缺失时回退到角色自身
            if (CameraTargetLookup.TryGetComponent(targetCharacterEntity, out CameraTarget CameraTarget) &&
                localToWorldLookup.TryGetComponent(CameraTarget.TargetEntity, out worldTransform))
            {
                foundValidCameraTarget = true;
            }
            else if (localToWorldLookup.TryGetComponent(targetCharacterEntity, out worldTransform))
            {
                foundValidCameraTarget = true;
            }

            return foundValidCameraTarget;
        }

        /// <summary>
        /// 根据目标上方向、平面前方向和俯仰角计算相机旋转
        /// </summary>
        /// <param name="targetUp">目标上方向</param>
        /// <param name="planarForward">目标平面内的前方向</param>
        /// <param name="pitchAngle">俯仰角</param>
        /// <returns>相机旋转</returns>
        public static quaternion CalculateCameraRotation(float3 targetUp, float3 planarForward, float pitchAngle)
        {
            quaternion pitchRotation = quaternion.Euler(math.right() * math.radians(pitchAngle));
            quaternion cameraRotation = MathUtilities.CreateRotationWithUpPriority(targetUp, planarForward);
            cameraRotation = math.mul(cameraRotation, pitchRotation);
            return cameraRotation;
        }

        /// <summary>
        /// 根据目标位置、相机旋转和距离计算相机位置
        /// </summary>
        /// <param name="targetPosition">相机目标位置</param>
        /// <param name="cameraRotation">相机旋转</param>
        /// <param name="distance">目标距离</param>
        /// <returns>相机世界位置</returns>
        public static float3 CalculateCameraPosition(float3 targetPosition, quaternion cameraRotation, float distance)
        {
            return targetPosition + (-MathUtilities.GetForwardFromRotation(cameraRotation) * distance);
        }
    }
}
