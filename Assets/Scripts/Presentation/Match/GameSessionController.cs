using UnityEngine;
using Unity.Entities;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Presentation.Match
{
    /// <summary>
    /// 负责结束当前网络会话并安全返回主菜单场景
    /// </summary>
    public static class GameSessionController
    {
        // 场景名必须与构建设置中的主菜单场景一致
        public static string MainMenuSceneName = "SCN_MainMenu";

        /// <summary>
        /// 销毁游戏网络世界、恢复时间系数并加载主菜单
        /// </summary>
        public static void ReturnToMainMenu()
        {
            // Host 同时持有服务器和客户端世界，因此需要逆序清理全部游戏世界
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

            // 场景切换前恢复时间，避免主菜单保持暂停
            Time.timeScale = 1f;

            // 仅在场景名有效时发起同步加载
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
}
