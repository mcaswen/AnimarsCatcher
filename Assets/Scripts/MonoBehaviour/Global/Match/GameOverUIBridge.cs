using UnityEngine;

namespace AnimarsCatcher.Presentation.Global
{
    /// <summary>
    /// 隔离 ECS 客户端系统与场景结算界面的直接依赖
    /// </summary>
    public static class GameOverUIBridge
    {
        /// <summary>
        /// 把客户端胜负结果转交给当前场景中的结算面板
        /// </summary>
        /// <param name="isWin">本地玩家是否获胜</param>
        public static void ShowGameOver(bool isWin)
        {
            if (GameResultPanelController.Instance != null)
            {
                GameResultPanelController.Instance.Show(isWin);
            }
            else
            {
                Debug.LogWarning("[GameOverUIBridge] No GameOverView.Instance in scene.");
            }
        }
    }
}
