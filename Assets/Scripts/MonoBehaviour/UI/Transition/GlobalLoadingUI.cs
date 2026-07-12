using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using AnimarsCatcher.Mono.Global;

public class GlobalLoadingUI : MonoBehaviour
{
    public static GlobalLoadingUI Instance { get; private set; }

    [Header("UI")]
    [FormerlySerializedAs("loadingCanvas")]
    [SerializeField] private Canvas _loadingCanvas;
    [FormerlySerializedAs("slideImages")]
    [SerializeField] private List<Image> _slideImages = new List<Image>();
    [FormerlySerializedAs("slideIntervalSeconds")]
    [SerializeField] private float _slideIntervalSeconds = 2f;
    [FormerlySerializedAs("minCoverSeconds")]
    [SerializeField] private float _minCoverSeconds = 10f; // 至少遮 10 秒

    private Coroutine _loadingRoutine;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (_loadingCanvas)
            _loadingCanvas.gameObject.SetActive(false);
    }

    public void StartLoadingAndTransition(string sceneName)
    {
        if (_loadingRoutine != null)
            return;

        _loadingRoutine = StartCoroutine(LoadingSequence(sceneName));
    }

    private IEnumerator LoadingSequence(string sceneName)
    {
        if (!_loadingCanvas)
        {
            Debug.LogWarning("[GlobalLoadingUI] loadingCanvas not set, fallback to direct LoadScene");
            SceneManager.LoadScene(sceneName);
            ClientCinematicState.ShouldRunIntro = true;
            yield break;
        }

        _loadingCanvas.gameObject.SetActive(true);
        SetupSlidesInitialState();

        // 异步加载战斗场景
        AsyncOperation loadOp = SceneManager.LoadSceneAsync(sceneName);
        loadOp.allowSceneActivation = false;

        float elapsed = 0f;
        int slideIndex = 0;

        while (true)
        {
            // 图片轮播
            if (_slideImages.Count > 0)
            {
                for (int i = 0; i < _slideImages.Count; i++)
                {
                    bool active = (i == slideIndex);
                    if (_slideImages[i])
                        _slideImages[i].gameObject.SetActive(active);
                }

                yield return new WaitForSeconds(_slideIntervalSeconds);
                elapsed += _slideIntervalSeconds;
                slideIndex = (slideIndex + 1) % _slideImages.Count;
            }
            else
            {
                // 没有图片就每帧检查
                elapsed += Time.deltaTime;
                yield return null;
            }

            // 加载已经基本完成 & 遮罩时间 >= 10s，就可以切场景了
            bool loadReady = loadOp.progress >= 0.9f; // Unity 的“加载完成但还未激活”
            if (loadReady && elapsed >= _minCoverSeconds)
                break;
        }

        // 准备进入战斗场景
        loadOp.allowSceneActivation = true;

        // 等待场景真正激活
        while (!loadOp.isDone)
            yield return null;

        // 场景激活后下一帧再关 UI，避免闪一下上一帧的画面
        yield return null;

        if (_loadingCanvas)
            _loadingCanvas.gameObject.SetActive(false);

        // 告诉战斗场景：要跑开场运镜
        ClientCinematicState.ShouldRunIntro = true;

        _loadingRoutine = null;
    }

    private void SetupSlidesInitialState()
    {
        if (_slideImages == null)
            return;

        foreach (var img in _slideImages)
        {
            if (img)
                img.gameObject.SetActive(false);
        }
    }
}
