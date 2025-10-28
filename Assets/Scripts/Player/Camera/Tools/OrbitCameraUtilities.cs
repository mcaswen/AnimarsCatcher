using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.CharacterController;
using UnityEngine;

public static class OrbitCameraUtilities
{
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

        // Camera target is either defined by the CameraTarget component, or if not, the transform of the followed character
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

    public static bool TryGetCameraTargetInterpolatedWorldTransform(
        Entity targetCharacterEntity,
        ref ComponentLookup<LocalToWorld> localToWorldLookup,
        ref ComponentLookup<CameraTarget> CameraTargetLookup,
        out LocalToWorld worldTransform)
    {
        bool foundValidCameraTarget = false;
        worldTransform = default;

        // Get the interpolated transform of the target
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

    public static quaternion CalculateCameraRotation(float3 targetUp, float3 planarForward, float pitchAngle)
    {
        quaternion pitchRotation = quaternion.Euler(math.right() * math.radians(pitchAngle));
        quaternion cameraRotation = MathUtilities.CreateRotationWithUpPriority(targetUp, planarForward);
        cameraRotation = math.mul(cameraRotation, pitchRotation);
        return cameraRotation;
    }

    public static float3 CalculateCameraPosition(float3 targetPosition, quaternion cameraRotation, float distance)
    {
        return targetPosition + (-MathUtilities.GetForwardFromRotation(cameraRotation) * distance);
    }
}

