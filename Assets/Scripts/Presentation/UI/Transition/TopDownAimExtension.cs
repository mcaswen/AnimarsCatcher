using UnityEngine;
using Cinemachine;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 在 Cinemachine Aim 阶段强制虚拟相机朝世界下方观察
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class TopDownAimExtension : CinemachineExtension
    {
        protected override void PostPipelineStageCallback(
            CinemachineVirtualCameraBase virtualCamera,
            CinemachineCore.Stage stage,
            ref CameraState state,
            float deltaTime)
        {
            // 仅在 Aim 阶段覆盖方向 避免影响位置和镜头参数计算
            if (stage != CinemachineCore.Stage.Aim)
                return;

            // 使用世界 Z 轴作为不与向下方向平行的上方向
            Vector3 forward = Vector3.down;
            Vector3 up = Vector3.forward;

            state.RawOrientation = Quaternion.LookRotation(forward, up);
        }
    }
}
