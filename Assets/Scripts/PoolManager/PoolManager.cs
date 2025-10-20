using System;
using UnityEngine;
using UnityEngine.Pool;

namespace AnimarsCatcher
{
    public class PoolManager : MonoBehaviour
    {
        public static PoolManager Instance { get; private set; }

        public bool CollectionChecks = true;
        public int MaxPoolSize = 20;

        private IObjectPool<GameObject> _BeamPool;
        public IObjectPool<GameObject> BeamPool
        {
            get
            {
                if (_BeamPool == null)
                {
                    _BeamPool = new ObjectPool<GameObject>(CreatePooledItem, OnTakeFromPool, OnReturnedToPool, OnDestroyPoolObject, CollectionChecks, 10, MaxPoolSize);
                }
                return _BeamPool;
            }
        }

        private void Awake()
        {
            Instance = this;
        }

        GameObject CreatePooledItem()
        {
            var go = Instantiate(Resources.Load<GameObject>(ResourcePath.FX_BeamPrefabPath));
            var fxBeam = go.AddComponent<FX_Beam>();
            fxBeam.BeamPool = BeamPool;
            return go;
        }

        private void OnReturnedToPool(GameObject gameObject)
        {
            gameObject.SetActive(false);
        }

        private void OnTakeFromPool(GameObject gameObject)
        {
            gameObject.SetActive(true);
        }

        private void OnDestroyPoolObject(GameObject gameObject)
        {
            Destroy(gameObject);
        }
    }
}