using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

public class CharacterViewFollower : MonoBehaviour
{
    public Entity CharacterEntity;
    EntityManager _entityManager;
    Animator _animator;
    Vector3 _lastPos;

    public void Bind(Entity entity, EntityManager entityManager) { CharacterEntity = entity; _entityManager = entityManager; }

    void Awake()
    {
        _animator = GetComponentInChildren<Animator>();
        if (_entityManager == default)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            _entityManager = world.EntityManager;
        }
        if (_animator) _animator.applyRootMotion = false;
        _lastPos = transform.position;
    }

    void Update()
    {
        Debug.LogWarning($"[View Follower] Update CharacterEntity={CharacterEntity}");

        if (_animator && _entityManager.Exists(CharacterEntity) && _entityManager.HasComponent<CharacterAnimationComponent>(CharacterEntity))
        {
            var animationParams = _entityManager.GetComponentData<CharacterAnimationComponent>(CharacterEntity);

            // 可见速度和渲染一致
            // var dt = Mathf.Max(Time.deltaTime, 1e-5f);

            Debug.LogWarning($"[View Follower] pos={transform.position} last={_lastPos}");

            // var planarDelta = Vector3.ProjectOnPlane(transform.position - _lastPos, Vector3.up);
            // float visualSpeed = planarDelta.magnitude / dt;

            _animator.SetFloat("Speed", animationParams.Speed);
            _animator.SetBool("Grounded", animationParams.Grounded);
            _animator.SetFloat("MoveX", animationParams.Move.x);
            _animator.SetFloat("MoveZ", animationParams.Move.z);
        }
        _lastPos = transform.position;
    }
    
    //每帧末尾更新位置，避免抖动
    void LateUpdate()
    {
        if (!_entityManager.Exists(CharacterEntity)) return;

        var lt = _entityManager.GetComponentData<LocalTransform>(CharacterEntity);
        transform.SetPositionAndRotation(lt.Position, lt.Rotation);

        var s = lt.Scale;
        transform.localScale = new Vector3(s, s, s);
    }

}
