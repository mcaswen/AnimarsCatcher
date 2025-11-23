using UnityEngine;

public static class GameOverUIBridge
{
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
