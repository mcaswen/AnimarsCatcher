using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace AnimarsCatcher
{
    public interface ICanShoot
    {
        bool CheckCanShoot(Vector3 position);
        bool HasDestroyed();
    }

    public class FragileItem : MonoBehaviour,ICanShoot, IResource
    {
        [SerializeField]
        private int _ResourceCount;
        public int ResourceCount => _ResourceCount;

        [SerializeField] private int _Health = 200;

        [SerializeField] private int _DamagePerShoot = 10;
        [SerializeField] private float _InstantiateRadius = 1;
        public ReactiveProperty<int> Health;
        public List<GameObject> PickableCrystals;

        private LayerMask _Mask;

        private LayerMask _SelfLayerMask;

        private void Awake()
        {
            _Mask = (1 << LayerMask.NameToLayer("Ani")) | (1 << LayerMask.NameToLayer("Player"));
            _Mask = ~_Mask;
            _SelfLayerMask = gameObject.layer;

            Health = new ReactiveProperty<int>(_Health);
        }

        private void Start()
        {
            Health.Subscribe(health => HandleDestroy(health));
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
             Vector2 randomOffset2D = Random.insideUnitCircle * _InstantiateRadius;
            Vector3 randomOffset = new Vector3(randomOffset2D.x, 0, randomOffset2D.y);

            return transform.position + randomOffset;

        }

        public void TakeDamage()
        {
            Health.Value -= _DamagePerShoot;
            _Health -= _DamagePerShoot;
        }

        public bool CheckCanShoot(Vector3 position)
        {
            Vector3 dir = transform.position - position;
            Physics.Raycast(position, dir, out var hitInfo, 30, _Mask);

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
            gameObject.layer = _SelfLayerMask;
        }
    }
}
