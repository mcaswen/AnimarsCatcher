using UnityEngine;
using Unity.Entities;
using UnityEngine.SceneManagement;

public static class GameSessionController
{
    // 主菜单场景名，在 Inspector 或别的地方赋值
    public static string MainMenuSceneName = "SCN_MainMenu";

    public static void ReturnToMainMenu()
    {
        // 1. 销毁所有 GameClient / GameServer 世界
        //    （Host 情况下会同时有 ServerSimulationWorld + ClientSimulationWorld）
        var worlds = World.All;

        for (int i = worlds.Count - 1; i >= 0; i--)
        {
            var world = worlds[i];
            if (world.IsCreated &&
                (world.Flags.HasFlag(WorldFlags.GameClient) ||
                 world.Flags.HasFlag(WorldFlags.GameServer)))
            {
                Debug.Log($"[GameSessionController] Disposing world: {world.Name}, Flags={world.Flags}");
                world.Dispose();
            }
        }

        // 2. 确保时间系数恢复正常
        Time.timeScale = 1f;

        // 3. 加载主界面场景
        if (!string.IsNullOrEmpty(MainMenuSceneName))
        {
            SceneManager.LoadScene(MainMenuSceneName);
        }
        else
        {
            Debug.LogError("[GameSessionController] MainMenuSceneName is not set.");
        }
    }
}
