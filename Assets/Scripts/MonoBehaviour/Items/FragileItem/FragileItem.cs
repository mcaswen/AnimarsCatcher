using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;
using AnimarsCatcher.Mono.Utilities;

namespace AnimarsCatcher.Mono.Items
{
    public interface ICanShoot
    {
        bool CheckCanShoot(Vector3 position);
        bool HasDestroyed();
    }

    public class FragileItem : MonoBehaviour, ICanShoot, IResource
    {
        [SerializeField]
        private int _resourceCount;
        public int ResourceCount => _resourceCount;

        [SerializeField] private int _health = 200;

        [SerializeField] private int _damagePerShoot = 10;
        [SerializeField] private float _instantiateRadius = 1;
        public ReactiveProperty<int> Health;
        public List<GameObject> PickableCrystals;

        private LayerMask _mask;

        private LayerMask _selfLayerMask;

        private void Awake()
        {
            _mask = (1 << LayerMask.NameToLayer("Ani")) | (1 << LayerMask.NameToLayer("Player"));
            _mask = ~_mask;
            _selfLayerMask = gameObject.layer;

            Health = new ReactiveProperty<int>(_health);
        }

        private void Start()
        {
            Health.Subscribe(HandleDestroy);
        }

        private void HandleDestroy(int health)
        {
            if (health > 0) return;

            Debug.Log($"[{gameObject.name}]: Destroyed! Instantiating {ResourceCount} items");

            for (int i = 0; i < ResourceCount; i++)
            {
                var spawnPosition = GetRandomPickableItemPosition();

                var pickableCrystal = Instantiate(
                    PickableCrystals[Random.Range(0, PickableCrystals.Count)],
                    spawnPosition,
                    Quaternion.identity
                );

                pickableCrystal.transform.localScale = 3 * Vector3.one;
            }

            Destroy(gameObject);
        }

        private Vector3 GetRandomPickableItemPosition()
        {
             Vector2 randomOffset2D = Random.insideUnitCircle * _instantiateRadius;
            Vector3 randomOffset = new Vector3(randomOffset2D.x, 0, randomOffset2D.y);

            return transform.position + randomOffset;

        }

        public void TakeDamage()
        {
            Health.Value -= _damagePerShoot;
            _health -= _damagePerShoot;
        }

        public bool CheckCanShoot(Vector3 position)
        {
            Vector3 dir = transform.position - position;
            Physics.Raycast(position, dir, out var hitInfo, 30, _mask);

            if (hitInfo.transform != null)
                return hitInfo.transform.CompareTag("FragileItem");
            
            return false;
        }

        public bool HasDestroyed()
        {
            return Health.Value <= 0;
        }

        private void OnMouseEnter()
        {
            gameObject.layer = LayerMask.NameToLayer("SelectedObject");
        }

        private void OnMouseExit()
        {
            gameObject.layer = _selfLayerMask;
        }
    }
}
