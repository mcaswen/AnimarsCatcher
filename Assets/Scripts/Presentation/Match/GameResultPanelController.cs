using UnityEngine;
using UnityEngine.UI;
using TMPro;
using AnimarsCatcher.Presentation.UI;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Match
{
    /// <summary>
    /// 展示本地玩家的对局结果并提供返回主菜单入口
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.Global", "AnimarsCatcher.Presentation", "GameResultPanelController")]
    public class GameResultPanelController : MonoBehaviour
    {
        public static GameResultPanelController Instance { get; private set; }

        [Header("引用")]
        [FormerlySerializedAs("RootPanel")]
        [SerializeField] private GameObject _rootPanel;
        [FormerlySerializedAs("ResultText")]
        [SerializeField] private TMP_Text _resultText;
        [FormerlySerializedAs("ReturnButton")]
        [SerializeField] private Button _returnButton;

        private bool _shown;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            if (_rootPanel != null)
                _rootPanel.SetActive(false);
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

            SmoothPanelView.ShowPanel(_rootPanel);

            if (_resultText != null)
                _resultText.text = isWin ? "VICTORY" : "DEFEAT";

            // Host 会同时暂停服务器和客户端世界，纯客户端只暂停本地模拟
            Time.timeScale = 0f;

            if (_returnButton != null)
            {
                _returnButton.onClick.RemoveListener(OnReturnClicked);
                _returnButton.onClick.AddListener(OnReturnClicked);
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
