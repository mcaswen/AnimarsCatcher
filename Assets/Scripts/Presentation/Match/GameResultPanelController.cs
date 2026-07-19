using UnityEngine;
using UnityEngine.UI;
using TMPro;
using AnimarsCatcher.Presentation.UI;

namespace AnimarsCatcher.Presentation.Global
{
    /// <summary>
    /// 展示本地玩家的对局结果并提供返回主菜单入口
    /// </summary>
    public class GameResultPanelController : MonoBehaviour
    {
        public static GameResultPanelController Instance { get; private set; }

        [Header("引用")]
        public GameObject RootPanel;   // 完整的对局结束面板
        public TMP_Text  ResultText;   // 显示本地玩家的胜利或失败结果
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

        /// <summary>
        /// 首次显示结算结果并暂停当前客户端表现
        /// </summary>
        /// <param name="isWin">本地玩家是否获胜</param>
        public void Show(bool isWin)
        {
            if (_shown)
                return;

            _shown = true;

            SmoothPanelView.ShowPanel(RootPanel);

            if (ResultText != null)
                ResultText.text = isWin ? "VICTORY" : "DEFEAT";

            // Host 会同时暂停服务器和客户端世界，纯客户端只暂停本地模拟
            Time.timeScale = 0f;

            if (ReturnButton != null)
            {
                ReturnButton.onClick.RemoveListener(OnReturnClicked);
                ReturnButton.onClick.AddListener(OnReturnClicked);
            }
        }

        private void OnReturnClicked()
        {
            // 离开结算界面前恢复全局时间系数
            Time.timeScale = 1f;
            _shown = false;

            // 网络世界清理由统一会话入口负责
            GameSessionController.ReturnToMainMenu();
        }
    }
}
