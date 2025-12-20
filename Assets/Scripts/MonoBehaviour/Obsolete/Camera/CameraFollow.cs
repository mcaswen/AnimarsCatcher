using UnityEngine;

namespace AnimarsCatcher.Mono
{
    [DefaultExecutionOrder(10000)] 
    public class CameraFollow : MonoBehaviour
    {
        [Header("目标设置")]
        [SerializeField] private string playerTag = "Player";
        [SerializeField] private Transform _Player;                 
        [SerializeField] private Vector3 pivotOffset = new Vector3(0f, 1.6f, 0f); 

        [Header("相机轨道")]
        [SerializeField] private float distance = 4f;              
        [SerializeField] private float minDistance = 1.5f;
        [SerializeField] private float maxDistance = 12f;
        [SerializeField] private Vector2 pitchLimits = new Vector2(-35f, 75f);

        [Header("输入/灵敏度")]
        [SerializeField] private float mouseSensitivity = 120f;       
        [SerializeField] private bool invertY = false;
        [SerializeField] private bool lockCursor = true;

        [Header("插值(秒)")]
        [SerializeField] private float pivotSmoothTime = 0.04f;       
        [SerializeField] private float angleSmoothTime = 0.05f;      
        [SerializeField] private float zoomSmoothTime  = 0.08f;     

        // 运行时状态
        private float yawTarget, pitchTarget, distTarget;
        private float yawSmooth,  pitchSmooth, distSmooth;
        private float yawVel,     pitchVel,    distVel;

        private Vector3 pivotWorldRaw;    
        private Vector3 pivotWorldSmooth;   // 平滑后的枢轴
        private Vector3 pivotVel;

        private void Start()
        {
            if (_Player == null)
            {
                var go = GameObject.FindWithTag(playerTag);
                if (go) _Player = go.transform;
            }
            if (!_Player)
            {
                Debug.LogError("[CameraFollow] Play Not Found");
                enabled = false;
                return;
            }

            var pivot = _Player.TransformPoint(pivotOffset);
            var toCam = transform.position - pivot;

            distTarget = distance = toCam.magnitude > 0.0001f ? toCam.magnitude : distance;
            var lookRot = Quaternion.LookRotation(-toCam, Vector3.up);

            var e = lookRot.eulerAngles;
            
            yawTarget   = yawSmooth   = e.y;
            pitchTarget = pitchSmooth = e.x > 180f ? e.x - 360f : e.x;

            pitchTarget = Mathf.Clamp(pitchTarget, pitchLimits.x, pitchLimits.y);
            pitchSmooth = Mathf.Clamp(pitchSmooth, pitchLimits.x, pitchLimits.y);

            distSmooth       = distTarget;
            pivotWorldRaw    = pivot;
            pivotWorldSmooth = pivot;

            if (lockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
            }
        }

        private void Update()
        {
            if (!_Player) return;

            float mx = Input.GetAxisRaw("Mouse X");
            float my = Input.GetAxisRaw("Mouse Y");

            yawTarget   += mx * mouseSensitivity * Time.deltaTime;
            pitchTarget += (invertY ? my : -my) * mouseSensitivity * Time.deltaTime;
            pitchTarget  = Mathf.Clamp(pitchTarget, pitchLimits.x, pitchLimits.y);

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            
            if (Mathf.Abs(scroll) > 0.0001f)
                distTarget = Mathf.Clamp(distTarget - scroll * 5f, minDistance, maxDistance);
        }

        private void LateUpdate()
        {
            if (!_Player) return;

            pivotWorldRaw = _Player.TransformPoint(pivotOffset);

            pivotWorldSmooth = Vector3.SmoothDamp(
                pivotWorldSmooth, pivotWorldRaw, ref pivotVel, pivotSmoothTime, Mathf.Infinity, Time.deltaTime);

            // 平滑角度与距离
            yawSmooth   = Mathf.SmoothDampAngle(yawSmooth,   yawTarget,   ref yawVel,   angleSmoothTime, Mathf.Infinity, Time.deltaTime);
            pitchSmooth = Mathf.SmoothDampAngle(pitchSmooth, pitchTarget, ref pitchVel, angleSmoothTime, Mathf.Infinity, Time.deltaTime);
            distSmooth  = Mathf.SmoothDamp(     distSmooth,  distTarget,  ref distVel,  zoomSmoothTime,  Mathf.Infinity, Time.deltaTime);

            // 由平滑后的枢轴/角度/距离得到期望相机位姿
            Quaternion rot   = Quaternion.Euler(pitchSmooth, yawSmooth, 0f);
            Vector3 desired  = pivotWorldSmooth + rot * (Vector3.back * distSmooth);

            //  直接设置
            transform.position = desired;
            transform.rotation = Quaternion.LookRotation(pivotWorldSmooth - transform.position, Vector3.up);
        }
    }
}
