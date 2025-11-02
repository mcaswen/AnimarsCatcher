using System;
using System.Collections;
using System.Collections.Generic;
using Cinemachine;
using UnityEngine;

namespace AnimarsCatcher.Mono
{
    public class CameraController : MonoBehaviour
    {
        [Header("Mouse Settings")]
        public float mouseSensitivity = 2f;
        public float offsetSensitivity = 0.5f;
        public bool invertY = false;
        
        [Header("Camera Clamping")]
        public float verticalMin = -10f;
        public float verticalMax = 60f;

        [Header("Offset Limits")]
        public float maxHorizontalOffset = 2f;
        public float maxVerticalOffset = 1f;
        
        private float currentX = 0f;
        private float currentY = 0f;
        private Vector3 initialLocalPosition;

        public float XSpeed = 300f;
        private CinemachineFreeLook _FreeLookCamera;
        public Transform cameraLookTarget;

        private void Awake()
        {
            _FreeLookCamera = GetComponent<CinemachineFreeLook>();
        }

        void Start()
        {
            // if (cameraLookTarget == null)
            // {
            //     GameObject go = new GameObject("CameraLookTarget");
            //     cameraLookTarget = go.transform;
            //     cameraLookTarget.SetParent(transform);
            //     cameraLookTarget.localPosition = Vector3.zero;
            // }

            // initialLocalPosition = cameraLookTarget.localPosition;

            // _FreeLookCamera.LookAt = cameraLookTarget;

        }

        private void Update()
        {
            // HandleCameraInput();

            if (Input.GetMouseButton(2))
            {
                _FreeLookCamera.m_XAxis.m_MaxSpeed = XSpeed;
            }
            else
            {
                _FreeLookCamera.m_XAxis.m_MaxSpeed = 0;
            }
        }

        // void HandleCameraInput()
        // {
        //     float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        //     float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        //     if (invertY)
        //         mouseY = -mouseY;

        //     currentX += mouseX;
        //     currentY -= mouseY;

        //     currentY = Mathf.Clamp(currentY, 0f, 1f);

        //     _FreeLookCamera.m_XAxis.Value = currentX;
        //     _FreeLookCamera.m_YAxis.Value = currentY;

        //     float offsetX = Input.GetAxis("Mouse X") * offsetSensitivity;
        //     float offsetY = Input.GetAxis("Mouse Y") * offsetSensitivity;

        //     Vector3 offset = cameraLookTarget.localPosition;
        //     offset.x = Mathf.Clamp(offset.x + offsetX, -maxHorizontalOffset, maxHorizontalOffset);
        //     offset.y = Mathf.Clamp(offset.y + offsetY, -maxVerticalOffset, maxVerticalOffset);
        //     offset.z = 0; 

        //     cameraLookTarget.localPosition = offset;
        // }
    }

}

