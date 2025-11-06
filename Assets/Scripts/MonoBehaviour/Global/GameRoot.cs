using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using AnimarsCatcher.Mono.UI;
using AnimarsCatcher.Mono.Items;
using AnimarsCatcher.Mono.Utilities;

namespace AnimarsCatcher.Mono.Global
{
    public class GameRoot : MonoBehaviour
    {
        private DetailedLevelInfo _info;
        private Timer _timer;
        private GameModel _gameModel;
        public GameModel GameModel => _gameModel;
        private AchievementSystem _achievementSystem;

        private GameObject _pickerAniPrefab;
        private GameObject _blasterAniPrefab;
        private int _spawningBlasterAniCount = 0;
        private int _spawningPickerAniCount = 0;

        private ReactiveProperty<int> _currentLevelTime;
        private int _levelTimerId = -1;
        private int _blueprintTimerId = -1;

        private Transform _homeTrans;
        public Transform Anis;
        public Animator EnvironmentAnimator;

        public Transform BluePrints;

        [MenuItem("Tools/Clear Save Data")]
        public static void ClearSaveData()
        {
            PlayerPrefs.DeleteAll();
        }

        private void Awake()
        {
            _homeTrans = GameObject.FindWithTag("Home").transform;
            _timer = new Timer();
            _gameModel = new GameModel();
            _achievementSystem = new AchievementSystem();
            _achievementSystem.Init(_gameModel);

            _pickerAniPrefab = Resources.Load<GameObject>(ResourcePath.PickerAniPath);
            _blasterAniPrefab = Resources.Load<GameObject>(ResourcePath.BlasterAniPath);

            string json = File.ReadAllText(ResourcePath.DetailedLevelInfoJson);
            _info = JsonUtility.FromJson<DetailedLevelInfo>(json);

            _currentLevelTime = new ReactiveProperty<int>(60);
            UIManager.Instance.Init(_gameModel, _currentLevelTime, _info.PickerAniFoodCostCount, _info.PickerAniCrystalCostCount,
                _info.BlasterAniFoodCostCount, _info.BlasterAniCrystalCostCount);
        }

        private void Start()
        {
            if (_gameModel.HasSaveData())
            {
                _gameModel.Load();
                LoadLevelFromSaveData();
            }
            else
            {
                _gameModel.Day.Value = 1;
                LoadLevel(1);
            }

            EventBus.Instance.Subscribe<LevelDayStartedEventData>(OnLevelDayStarted);

        }

        private void OnDestroy()
        {
            EventBus.Instance.Unsubscribe<LevelDayStartedEventData>(OnLevelDayStarted);
        }

        private void Update()
        {
            _timer.Update();

            if (Input.GetKeyDown(KeyCode.Space))
            {
                Debug.Log("Manually Loaded Next Level");
                EventBus.Instance.Publish(new LevelDayEndedEventData());
            }
        }

        private void LoadNextLevel()
        {
            int day = ++_gameModel.Day.Value;
            LoadLevel(day);
        }

        private void LoadLevel(int day)
        {
            Debug.Log($"loading level day: {day}");

            DetailedLevelData currentLevelData = _info.LevelDatas[day - 1];

            LoadMap(currentLevelData);
            StartCoroutine(SpawnAnis(_spawningPickerAniCount, _spawningBlasterAniCount));

            _gameModel.PickerAniCount.Value += _spawningPickerAniCount;
            _gameModel.BlasterAniCount.Value += _spawningBlasterAniCount;

            EnvironmentAnimator.Rebind();
            EnvironmentAnimator.Play("Light");
            StartTimer(currentLevelData.LevelTime);
        }

        private void LoadLevelFromSaveData()
        {
            Debug.Log($"loading level from: {_gameModel.Day}");

            DetailedLevelData levelData = _info.LevelDatas[_gameModel.Day.Value - 1];
            LoadMap(levelData);

            StartCoroutine(SpawnAnis(_gameModel.PickerAniCount.Value, _gameModel.BlasterAniCount.Value));

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
            if (_levelTimerId != -1)
            {
                _timer.DeleteTask(_levelTimerId);
                _levelTimerId = -1;
            }
            if (_blueprintTimerId != -1)
            {
                _timer.DeleteTask(_blueprintTimerId);
                _blueprintTimerId = -1;
            }

            _currentLevelTime.Value = seconds;

            _levelTimerId = _timer.AddTask(RecordLevelTimeTask, 1, -1);
            _blueprintTimerId = _timer.AddTask(RecordBlueprintSpawning, 30, 1);
        }

        private void RecordLevelTimeTask(int id)
        {
            Debug.Log($"Remaining Second:{_currentLevelTime.Value}");
            _currentLevelTime.Value -= 1;
            if (_currentLevelTime.Value <= 0)
            {
                EventBus.Instance.Publish(new LevelDayEndedEventData());
                _timer.DeleteTask(id);
            }
        }

        private void RecordBlueprintSpawning(int id)
        {
            int random = Random.Range(0, 2);
            if (random == 0)
            {
                GetOneBlueprint();
                _timer.DeleteTask(id);
            }
        }

        private void GetOneBlueprint()
        {
            int childCount = BluePrints.childCount;
            if (childCount != 0)
            {
                var child = BluePrints.GetChild(Random.Range(0, BluePrints.childCount));
                if (!child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(true);
                    child.gameObject.AddComponent<Blueprint>();
                }
            }
        }

        private IEnumerator SpawnAnis(int pickerAniCount, int blasterAniCount)
        {
            for (int i = 0; i < pickerAniCount; i++)
            {
                yield return new WaitForSeconds(1f);

                AchievementManager.Instance.RecordFirstPickerAniCollected();

                var position = new Vector3(_homeTrans.position.x - Random.Range(1, 3),
                    _homeTrans.position.y,
                    _homeTrans.position.z + Random.Range(-3, 3));
                var ani = Instantiate(_pickerAniPrefab, position, Quaternion.identity, Anis);
                ani.GetComponent<NavMeshAgent>().SetDestination(position);
            }

            for (int i = 0; i < blasterAniCount; i++)
            {
                yield return new WaitForSeconds(1f);
                var position = new Vector3(_homeTrans.position.x - Random.Range(1, 3),
                    _homeTrans.position.y,
                    _homeTrans.position.z + Random.Range(-3, 3));
                var ani = Instantiate(_blasterAniPrefab, position, Quaternion.identity, Anis);
                ani.GetComponent<NavMeshAgent>().SetDestination(position);
            }
        }

        private void OnApplicationQuit()
        { }

        private void OnLevelDayStarted(LevelDayStartedEventData eventData)
        {
            _spawningBlasterAniCount = eventData.SpawningBlasterAniCount;
            _spawningPickerAniCount = eventData.SpawningPickerAniCount;
            LoadNextLevel();
        }

    }
}