using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GameResultPanelController : MonoBehaviour
{
    public static GameResultPanelController Instance { get; private set; }

    [Header("引用")]
    public GameObject RootPanel;   // 整个 GameOver 面板
    public TMP_Text  ResultText;   // "Victory" / "Defeat"
    public Button ReturnButton; // 返回主界面

    private bool _shown;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (RootPanel != null)
            RootPanel.SetActive(false);
    }

    public void Show(bool isWin)
    {
        if (_shown)
            return;

        _shown = true;

        SmoothPanelView.ShowPanel(RootPanel);

        if (ResultText != null)
            ResultText.text = isWin ? "VICTORY" : "DEFEAT";

        // 对 Host 来说会同时停 Server+Client 世界；对纯 Client 来说只停本地模拟。
        Time.timeScale = 0f;

        if (ReturnButton != null)
        {
            ReturnButton.onClick.RemoveListener(OnReturnClicked);
            ReturnButton.onClick.AddListener(OnReturnClicked);
        }
    }

    private void OnReturnClicked()
    {
        // 恢复时间
        Time.timeScale = 1f;
        _shown = false;

        // 交给统一的 Session 管理做收尾
        GameSessionController.ReturnToMainMenu();
    }
}
