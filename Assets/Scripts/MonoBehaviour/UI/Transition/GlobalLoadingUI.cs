using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using AnimarsCatcher.Mono.Global;

/// <summary>
/// 跨场景保留的加载遮罩和轮播图控制器
/// 在最低展示时间和异步加载同时满足后激活目标场景
/// </summary>
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
    [SerializeField] private float _minCoverSeconds = 10f;

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

    /// <summary>
    /// 启动一次不可重入的场景加载流程
    /// </summary>
    /// <param name="sceneName">目标场景名称</param>
    public void StartLoadingAndTransition(string sceneName)
    {
        if (_loadingRoutine != null)
            return;

        _loadingRoutine = StartCoroutine(LoadingSequence(sceneName));
    }

    // 同时推进图片轮播和异步加载 达到最低遮罩时间后才允许激活场景
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

        // 延迟场景激活以保证遮罩覆盖整个加载过程
        AsyncOperation loadOp = SceneManager.LoadSceneAsync(sceneName);
        loadOp.allowSceneActivation = false;

        float elapsed = 0f;
        int slideIndex = 0;

        while (true)
        {
            // 每个轮播周期只激活当前图片
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
                // 没有轮播图时按帧累计展示时间
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Unity 异步加载在等待激活时进度停留在 0.9
            bool loadReady = loadOp.progress >= 0.9f;
            if (loadReady && elapsed >= _minCoverSeconds)
                break;
        }

        // 两个条件都满足后允许 Unity 激活目标场景
        loadOp.allowSceneActivation = true;

        // 等待目标场景完成激活
        while (!loadOp.isDone)
            yield return null;

        // 延迟一帧关闭遮罩 避免显示场景切换前的残留画面
        yield return null;

        if (_loadingCanvas)
            _loadingCanvas.gameObject.SetActive(false);

        // 通知新场景在初始化后播放开场运镜
        ClientCinematicState.ShouldRunIntro = true;

        _loadingRoutine = null;
    }

    // 隐藏所有轮播图 由加载循环激活第一张
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
