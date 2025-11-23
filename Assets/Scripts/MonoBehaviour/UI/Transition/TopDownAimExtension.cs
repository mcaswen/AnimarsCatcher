using UnityEngine;
using Cinemachine;

/// <summary>
/// 强制虚拟相机永远“向下看”：forward = Vector3.down。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class TopDownAimExtension : CinemachineExtension
{
    protected override void PostPipelineStageCallback(
        CinemachineVirtualCameraBase vcam,
        CinemachineCore.Stage stage,
        ref CameraState state,
        float deltaTime)
    {
        // 只在 Aim 阶段改朝向
        if (stage != CinemachineCore.Stage.Aim)
            return;

        // forward 朝向世界 -Y，up 随便给个和 down 不平行的轴，这里用世界 +Z
        Vector3 forward = Vector3.down;
        Vector3 up = Vector3.forward;

        state.RawOrientation = Quaternion.LookRotation(forward, up);
    }
}
