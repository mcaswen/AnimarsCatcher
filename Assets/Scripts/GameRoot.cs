using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace AnimarsCatcher
{
    public class GameRoot : MonoBehaviour
    {
        private DetailedLevelInfo _Info;
        private Timer _Timer;
        private GameModel _GameModel;
        public GameModel GameModel => _GameModel;
        private AchievementSystem _AchievementSystem;

        private GameObject _PickerAniPrefab;
        private GameObject _BlasterAniPrefab;

        private Transform _HomeTrans;
        public Transform Anis;
        public Animator EnvironmentAnimator;

        [MenuItem("Tools/Clear Save Data")]
        public static void ClearSaveData()
        {
            PlayerPrefs.DeleteAll();
        }

        private void Awake()
        {
            _HomeTrans = GameObject.FindWithTag("Home").transform;
            _Timer = new Timer();
            _GameModel = new GameModel();
            _AchievementSystem = new AchievementSystem();
            _AchievementSystem.Init(_GameModel);

            _PickerAniPrefab = Resources.Load<GameObject>(ResourcePath.PickerAniPath);
            _BlasterAniPrefab = Resources.Load<GameObject>(ResourcePath.BlasterAniPath);

            string json = File.ReadAllText(ResourcePath.DetailedLevelInfoJson);
            _Info = JsonUtility.FromJson<DetailedLevelInfo>(json);
        }

        private void Start()
        {

            if (_GameModel.HasSaveData())
            {
                _GameModel.Load();
                LoadLevelFromSaveData();
            }
            else
            {
                _GameModel.Day.Value = 1;
                LoadLevel(1);
            }
            GameModel.BlueprintCount.Subscribe(count =>
            {
                if (count == 9)
                {
                    Debug.Log("Mission Completed!");
                }
            });
        }

        private void Update()
        {
            _Timer.Update();

            if (Input.GetKeyDown(KeyCode.Space))
            {
                Debug.Log("Manually Loaded Next Level");
                LoadNextLevel();
            }
        }

        private void LoadNextLevel()
        {
            int day = ++_GameModel.Day.Value;
            LoadLevel(day);
        }

        private void LoadLevel(int day)
        {
            Debug.Log($"load level day: {day}");

            DetailedLevelData levelData = _Info.LevelDatas[day - 1];
            
            LoadMap(levelData);
            StartCoroutine(SpawnAnis(levelData.PickerAniCount, levelData.BlasterAniCount));
            
            _GameModel.PickerAniCount.Value += levelData.PickerAniCount;
            _GameModel.BlasterAniCount.Value += levelData.BlasterAniCount;

            EnvironmentAnimator.Rebind();
            EnvironmentAnimator.Play("Light");
            StartTimer(levelData.LevelTime);
        }

        private void LoadLevelFromSaveData()
        {
            Debug.Log($"load level from: {_GameModel.Day}");

            DetailedLevelData levelData = _Info.LevelDatas[_GameModel.Day.Value - 1];
            LoadMap(levelData);

            StartCoroutine(SpawnAnis(_GameModel.PickerAniCount.Value, _GameModel.BlasterAniCount.Value));

            EnvironmentAnimator.Rebind();
            EnvironmentAnimator.Play("Light");

            StartTimer(levelData.LevelTime);
        }

        private void LoadMap(LevelData levelData)
        {
            Vector2 mapSize = new Vector2(levelData.X, levelData.Y);

            MapManager.Instance.LoadItems(mapSize, levelData.FoodNum, 2, ResourcePath.FoodPrefabPath);
            MapManager.Instance.LoadItems(mapSize, levelData.CrystalNum, 2, ResourcePath.CrystalPrefabPath);

        }

        private void LoadMap(DetailedLevelData levelData)
        {
            foreach (var mapResource in levelData.Resources)
            {
                Vector2 mapPosition = new(mapResource.Area[0], mapResource.Area[1]);
                Vector2 mapSize = new(mapResource.Area[2], mapResource.Area[3]);
                MapManager.Instance.LoadItems(mapPosition, mapSize, mapResource.FoodNum, 2, ResourcePath.FoodPrefabPath);
                MapManager.Instance.LoadItems(mapPosition, mapSize, mapResource.CrystalNum, 2, ResourcePath.CrystalPrefabPath);
            }
        }

        private void StartTimer(int seconds)
        {
            _Timer.AddTask(id =>
            {
                // Debug.Log($"Remaining Second:{seconds}");
                seconds -= 1;
                if (seconds <= 0)
                {
                    LoadNextLevel();
                    _Timer.DeleteTask(id);
                }
            }, 1, seconds);

            _Timer.AddTask(id => 
            {
                int random = Random.Range(0, 2);
                if (random == 0)
                {
                    //GetOneBlueprint();
                    _Timer.DeleteTask(id);
                }
            }, 30, 1);
        }

        // private void GetOneBlueprint()
        // {
        //     int childCount = BluePrints.childCount;
        //     if (childCount != 0)
        //     {
        //         var child = BluePrints.GetChild(Random.Range(0, BluePrints.childCount));
        //         if (!child.gameObject.activeSelf)
        //         {
        //             child.gameObject.SetActive(true);
        //             child.gameObject.AddComponent<Blueprint>();
        //         }
        //     }
        // }

        private IEnumerator SpawnAnis(int pickerAniCount, int blasterAniCount)
        {
            for (int i = 0; i < pickerAniCount; i++)
            {
                yield return new WaitForSeconds(1f);

                AchievementManager.Instance.RecordFirstPickerAniCollected();
                
                var position = new Vector3(_HomeTrans.position.x - Random.Range(1, 3),
                    _HomeTrans.position.y,
                    _HomeTrans.position.z + Random.Range(-3, 3));
                var ani = Instantiate(_PickerAniPrefab, position, Quaternion.identity, Anis);
                ani.GetComponent<NavMeshAgent>().SetDestination(position);
            }

            for (int i = 0; i < blasterAniCount; i++)
            {
                yield return new WaitForSeconds(1f);
                var position = new Vector3(_HomeTrans.position.x - Random.Range(1, 3),
                    _HomeTrans.position.y,
                    _HomeTrans.position.z + Random.Range(-3, 3));
                var ani = Instantiate(_BlasterAniPrefab, position, Quaternion.identity, Anis);
                ani.GetComponent<NavMeshAgent>().SetDestination(position);
            }
        }

        private void OnApplicationQuit()
        {
            
        }
    }
}