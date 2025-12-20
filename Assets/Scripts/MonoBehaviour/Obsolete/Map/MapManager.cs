using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;
using AnimarsCatcher.Mono.Items;

namespace AnimarsCatcher.Mono
{
    public class MapManager : MonoBehaviour
    {
        public static MapManager Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(this);
        }

        [SerializeField] private float _mapMaxHeight = 20;
        [SerializeField] private int _maxTryGetRandomLocationCount = 20;
        [SerializeField] private string _terrainTag = "Plane";
        private Transform _parentTransform;
        private List<Transform> _ttemList = new List<Transform>(); // 存储当前所有已刷新的Item
        private Queue<LoadTask> _loadTasks = new Queue<LoadTask>();
        
        private struct LoadTask
        {
            public string path;
            public Vector2 mapPosition;
            public Vector2 mapSize;
            public int count;
            public float minDistance;
        }

        public void LoadItems(Vector2 mapSize, int count, float minDistance, string path)
        {
            _loadTasks.Enqueue(new LoadTask()
            {
                path = path,
                mapSize = mapSize,
                count = count,
                minDistance = minDistance
            });
            
            if(_loadTasks.Count == 1)
            {
                StartNextLoadTask();
            }
        }

        private void StartNextLoadTask()
        {
            if(_loadTasks.Count == 0) return;

            var task = _loadTasks.Dequeue();
            _ttemList.Clear();

            if (_parentTransform == null)
            {
                _parentTransform = new GameObject("Items").transform;
            }

            List<GameObject> currentTaskPrefabs = Resources.LoadAll<GameObject>(task.path).ToList();
            if (currentTaskPrefabs.Count == 0)
            {
                Debug.LogError("Prefab Resources Fail or PrefabName is Empty");
                return;
            }

            StartCoroutine(LoadItemsCoroutine(task.mapPosition, task.mapSize, task.count, task.minDistance, currentTaskPrefabs));
        }

        IEnumerator LoadItemsCoroutine(Vector2 mapSize, int count, float minDistance, List<GameObject> prefabList)
        {
            yield return null;
            int resourceCount = 0;
            while (resourceCount < count)
            {
                var itemPrefab = prefabList[Random.Range(0, prefabList.Count)].transform;

                if (itemPrefab.TryGetComponent<IResource>(out var resource))
                    resourceCount += resource.ResourceCount;

                Instantiate(itemPrefab, _parentTransform).localPosition = GetRandomPosition(mapSize, minDistance);
                _ttemList.Add(itemPrefab);
                yield return null;
            }

            StartNextLoadTask();
        }

        Vector3 GetRandomPosition(Vector2 mapSize, float minDistance)
        {
            for (int i = 0; i < _maxTryGetRandomLocationCount; i++)
            {
                Vector3 randomPos = new Vector3(Random.Range(0, mapSize.x)
                    , _mapMaxHeight + 10, Random.Range(0, mapSize.y));

                bool isTooClose = false;

                foreach (var item in _ttemList)
                {
                    if (Vector3.Distance(new Vector3(randomPos.x, 0, randomPos.z),
                            new Vector3(item.position.x, 0, item.position.z)) < minDistance)
                    {
                        isTooClose = true;
                        break;
                    }
                }

                if (!isTooClose)
                {
                    Ray ray = new Ray(randomPos, Vector3.down);
                    if (Physics.Raycast(ray, out RaycastHit hit)
                        && hit.transform.CompareTag(_terrainTag)
                        && hit.point.y < 1)
                    {
                        return new Vector3(randomPos.x, hit.point.y, randomPos.z);
                    }
                }
            }
            
            Debug.LogWarning("Failed to find a suitable random position after maximum attempts");
            return new Vector3(Random.Range(0, mapSize.x), 0, Random.Range(0, mapSize.y));
        }

        #region Load Items With Area
        public void LoadItems(Vector2 mapPosition, Vector2 mapSize, int count, float minDistance, string path)
        {
            _loadTasks.Enqueue(new LoadTask()
            {
                path = path,
                mapPosition = mapPosition,
                mapSize = mapSize,
                count = count,
                minDistance = minDistance
            });
            
            if (_loadTasks.Count == 1)
                StartNextLoadTask();
        }

        IEnumerator LoadItemsCoroutine(Vector2 mapPosition, Vector2 mapSize, int count, float minDistance, List<GameObject> prefabList)
        {
            yield return null;
            int resourceCount = 0;

            while (resourceCount < count)
            {
                var itemPrefab = prefabList[Random.Range(0, prefabList.Count)].transform;

                if (itemPrefab.TryGetComponent<IResource>(out var resource))
                    resourceCount += resource.ResourceCount;

                Instantiate(itemPrefab, _parentTransform).localPosition = GetRandomPosition(mapPosition, mapSize, minDistance);
                _ttemList.Add(itemPrefab);

                yield return null;
            }
            
            StartNextLoadTask();
        }

        Vector3 GetRandomPosition(Vector2 mapPosition, Vector2 mapSize, float minDistance)
        {
            for (int i = 0; i < _maxTryGetRandomLocationCount; i++)
            {
                Vector3 randomPos = new Vector3(Random.Range(mapPosition.x, mapPosition.x + mapSize.x)
                    , _mapMaxHeight + 10, Random.Range(mapPosition.y, mapPosition.y + mapSize.y));

                bool isTooClose = false;
                
                foreach (var item in _ttemList)
                {
                    if (Vector3.Distance(new Vector3(randomPos.x, 0, randomPos.z),
                            new Vector3(item.position.x, 0, item.position.z)) < minDistance)
                    {
                        isTooClose = true;
                        break;
                    }
                }

                if (!isTooClose)
                {
                    Ray ray = new Ray(randomPos, Vector3.down);
                    if (Physics.Raycast(ray, out RaycastHit hit)
                        && hit.transform.CompareTag(_terrainTag)
                        && hit.point.y < 1)
                    {
                        return new Vector3(randomPos.x, hit.point.y, randomPos.z);
                    }
                }
            }
            Debug.LogError("Failed to find a suitable random position after maximum attempts");
            return new Vector3(Random.Range(0, mapSize.x), 0, Random.Range(0, mapSize.y));
        }
        #endregion
    }
}