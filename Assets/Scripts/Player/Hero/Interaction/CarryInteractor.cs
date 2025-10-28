// using UnityEngine;
// using UnityEngine.Animations.Rigging;
// using UnityEngine.InputSystem;

// public class CarryInteractor : MonoBehaviour
// {
//     [Header("Rig & IK")]
//     public Rig handsRig;                       // 只控制手的Rig
//     public TwoBoneIKConstraint leftIK;
//     public TwoBoneIKConstraint rightIK;

//     [Header("Sockets")]
//     public Transform carrySocket;              // 抱起后的跟随挂点（胸口/右手）

//     [Header("Blend")]
//     public float ikBlendTime = 0.15f;          // IK 进出时长
//     public float holdRigWeight = 0.7f;         // 抱起后手臂保持的权重

//     Grabbable current;

//     public Grabbable debugTarget;

//     // [ContextMenu("DEBUG / Pickup")]
//     // void DebugPickupE() { if (debugTarget && current == null && Keyboard.current.eKey.isPressed) Debug.LogWarning("Start Pick"); StartCoroutine(Pickup(debugTarget)); }
//     // [ContextMenu("DEBUG / Putdown")]
//     // void DebugPutdownQ() { if (current && Keyboard.current.qKey.isPressed) StartCoroutine(Putdown()); }

//     void Start()
//     {
//         if (debugTarget && current == null && Keyboard.current.eKey.isPressed) Debug.LogWarning("Start Pick"); StartCoroutine(Pickup(debugTarget));
//     }

//     void Update()
//     {

//         //if (debugTarget && current == null && Keyboard.current.eKey.isPressed) Debug.LogWarning("Start Pick"); StartCoroutine(Pickup(debugTarget));

//         // 正在锁定目标时，目标移动的话持续跟随抓握点
//         if (current != null)
//         {
//             var L = current.leftGrip; var R = current.rightGrip;
//             if (L) { leftIK.data.target.position = L.position; leftIK.data.target.rotation = L.rotation; }
//             if (R) { rightIK.data.target.position = R.position; rightIK.data.target.rotation = R.rotation; }
//         }

//         if (current && Keyboard.current.qKey.isPressed) StartCoroutine(Putdown());

//     }

//     public void Toggle(Grabbable g)
//     {
//         if (current == null) StartCoroutine(Pickup(g));
//         else StartCoroutine(Putdown());
//     }

//     System.Collections.IEnumerator Pickup(Grabbable g)
//     {
//         current = g;
//         g.BeginPickup(); // 置为Kinematic等

//         // 1) 把IK目标对齐到物体抓握点
//         AlignIKToGrips(g);

//         // 2) 淡入IK，让手“贴”到箱子
//         yield return FadeRig(handsRig, 0f, 1f, ikBlendTime);

//         // 3) 吸附：把箱子对齐CarryPose并成为子物体（或用约束平滑）
//         g.Attach(carrySocket);

//         // 4) 手稍微放松些，避免太僵硬
//         handsRig.weight = holdRigWeight;
//     }

//     System.Collections.IEnumerator Putdown()
//     {
//         // 1) 解除父子、恢复刚体
//         current.Detach();

//         // 2) 淡出IK，返回普通上半身动画
//         yield return FadeRig(handsRig, handsRig.weight, 0f, ikBlendTime);

//         current = null;
//     }

//     void AlignIKToGrips(Grabbable g)
//     {
//         if (g.leftGrip)
//         {
//             leftIK.data.target.position = g.leftGrip.position;
//             leftIK.data.target.rotation = g.leftGrip.rotation;
//         }
//         if (g.rightGrip)
//         {
//             rightIK.data.target.position = g.rightGrip.position;
//             rightIK.data.target.rotation = g.rightGrip.rotation;
//         }
//     }

//     System.Collections.IEnumerator FadeRig(Rig rig, float from, float to, float time)
//     {
//         float t = 0f;
//         while (t < time)
//         {
//             t += Time.deltaTime;
//             rig.weight = Mathf.Lerp(from, to, t / time);
//             yield return null;
//         }
//         rig.weight = to;
//     }
// }
