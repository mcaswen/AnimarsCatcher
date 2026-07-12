using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using UnityEngine;
using UnityEngine.Serialization;
using AnimarsCatcher.Mono.Items;
using AnimarsCatcher.Mono.Utilities;

namespace AnimarsCatcher.Mono
{
    public class Player : MonoBehaviour
    {
        public float MoveSpeed = 20f;

        public float ControlRadiusMin = 0f;
        public float ControlRadiusMax = 5f;

        [FormerlySerializedAs("TargetPosGO")]
        public GameObject TargetPositionObject;

        private float _currentRadius;
        private Vector3 _targetPosition;
        private bool _isRightMouseButtonHeld;

        private List<PICKER_Ani> _pickerAnis = new List<PICKER_Ani>();
        private List<BLASTER_Ani> _blasterAnis = new List<BLASTER_Ani>();

        //Components
        private Rigidbody _rigidbody;
        private CharacterController _characterController;
        
        //MainCamera
        private Camera _mainCamera;

        // Group Behaviour
        private Dictionary<Transform, int> _formationIndices = new();

        // Smoke
        [FormerlySerializedAs("FX_SmokeParticleSystem")]
        public ParticleSystem SmokeParticleSystem;

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _characterController = GetComponent<CharacterController>();
            _mainCamera = Camera.main;
        }

        void Update()
        {
            RobotMove();
            DrawRayFromScreenCenter();
            if (Input.GetMouseButton(1))
            {
                _isRightMouseButtonHeld = true;
                _targetPosition = GetMouseWorldPosition();
                GetControlAnis();
            }
            else
            {
                _isRightMouseButtonHeld = false;
            }
            AssignAniToCarry();
            AssignAniToShoot();

            _currentRadius = Mathf.Lerp(_currentRadius, _isRightMouseButtonHeld ? ControlRadiusMax : ControlRadiusMin,
                Time.deltaTime * 10f);
            TargetPositionObject.transform.position = GetMouseWorldPosition();
            TargetPositionObject.transform.Find("Cylinder").localScale = Vector3.one * (2 * _currentRadius);
        }
        
        private void DrawRayFromScreenCenter()
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null) return;
            
            Ray centerRay = mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            Debug.DrawRay(centerRay.origin, centerRay.direction * 50f, UnityEngine.Color.red);
        }

        private void FixedUpdate()
        {
            SetDestinations();
        }

        private void RobotMove()
        {
            float horizontalInput = Input.GetAxis("Horizontal");
            float verticalInput = Input.GetAxis("Vertical");
            float y = _mainCamera.transform.rotation.eulerAngles.y;

            Vector3 targetDirection = new Vector3(horizontalInput, 0, verticalInput);
            targetDirection = Quaternion.Euler(0, y, 0) * targetDirection;

            if (targetDirection != Vector3.zero)
                transform.forward = Vector3.Lerp(transform.forward, targetDirection, 10f * Time.deltaTime);
            
            var speed = targetDirection * MoveSpeed;
            //_rigidbody.velocity = speed;
            _characterController.SimpleMove(speed);

            ControlSmokeParticleSystem(speed);
        }

        private void GetControlAnis()
        {
            Collider[] hitColliders = new Collider[50];
            int colliderCount = Physics.OverlapSphereNonAlloc(_targetPosition, _currentRadius, hitColliders);
            for (int i = 0; i < colliderCount; i++)
            {
                if (hitColliders[i].CompareTag("PICKER_Ani"))
                {
                    var pickerAni = hitColliders[i].GetComponent<PICKER_Ani>();
                    if (!_pickerAnis.Contains(pickerAni))
                    {
                        _pickerAnis.Add(pickerAni);
                        pickerAni.IsFollow = true;
                        _formationIndices.Add(pickerAni.transform, _formationIndices.Count);
                    }
                }else if (hitColliders[i].CompareTag("BLASTER_Ani"))
                {
                    var blasterAni = hitColliders[i].GetComponent<BLASTER_Ani>();
                    if (!_blasterAnis.Contains(blasterAni))
                    {
                        _blasterAnis.Add(blasterAni);
                        blasterAni.IsFollow = true;
                        _formationIndices.Add(blasterAni.transform, _formationIndices.Count);
                    }
                }
            }
        }

        private void AssignAniToCarry()
        {
            if (Input.GetMouseButtonDown(0))
            {
                RaycastHit hit;
                Debug.Log("ray casted");

                Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                // Ray ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));

                int mask = ~LayerMask.GetMask("Player", "Ani");
                if (Physics.Raycast(ray, out hit, 100f, mask, QueryTriggerInteraction.Ignore))
                {
                    Debug.Log($"ray casted: {hit.collider.gameObject.name}");
                    if (hit.collider.CompareTag("PickableItem"))
                    {
                        var pickerAni = ChooseOnePickerAni();
                        if (pickerAni != null)
                        {
                            pickerAni.IsPick = true;
                            pickerAni.PickableItem = hit.collider.gameObject.GetComponent<PickableItem>();
                        }
                    }
                }
            }
        }

        private PICKER_Ani ChooseOnePickerAni()
        {
            foreach (var pickerAni in _pickerAnis)
            {
                if (!pickerAni.IsPick)
                {
                    return pickerAni;
                }
            }

            return null;
        }

        private void AssignAniToShoot()
        {
            if (Input.GetMouseButtonDown(0))
            {
                RaycastHit hit;
                Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                // Ray ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));

                int mask = ~LayerMask.GetMask("Player", "Ani");

                if (Physics.Raycast(ray, out hit, 50f, mask, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.CompareTag("FragileItem"))
                    {
                        var blasterAni = ChooseOneBlasterAni();
                        if (blasterAni != null)
                        {
                            blasterAni.IsShoot = true;
                            blasterAni.FragileItem = hit.collider.gameObject.GetComponent<FragileItem>();
                        }
                    }
                }
            }
        }
        
        private BLASTER_Ani ChooseOneBlasterAni()
        {
            foreach (var blasterAni in _blasterAnis)
            {
                if (!blasterAni.IsShoot)
                {
                    return blasterAni;
                }
            }
            return null;
        }



        private Vector3 GetMouseWorldPosition()
        {
            Ray ray = _mainCamera.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 200))
            {
                return hit.point;
            }
            return Vector3.zero;
        }
        
        private void SetDestinations()
        {
            _pickerAnis.ForEach(item => item.Destination = FollowUtility.RectArrange(transform, _formationIndices[item.transform]));
            _blasterAnis.ForEach(item => item.Destination = FollowUtility.RectArrange(transform, _formationIndices[item.transform]));
        }

        private void ControlSmokeParticleSystem(Vector3 speed)
        {
            if (speed.sqrMagnitude <= 0f && SmokeParticleSystem.isPlaying)
            {
                SmokeParticleSystem.Stop();
            } else if (speed.sqrMagnitude > 0f)
            {
                SmokeParticleSystem.transform.forward = -speed;
                SmokeParticleSystem.Play();
            }
        }
    }
}

