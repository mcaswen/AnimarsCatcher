#if UNITY_EDITOR
using Unity.NetCode.Hybrid;

namespace Unity.NetCode.Tests
{
    /// <summary>
    /// 用于构建 PlayMode 测试的辅助方法
    /// </summary>
    internal static class PlaymodeUtils
    {
        /// <summary>
        /// 将当前构建目标设置为纯客户端
        /// 可在命令行启动 Editor 时传入 "-executeMethod Unity.NetCode.Tests.PlaymodeUtils.SetClientBuild" 并于构建前执行
        /// </summary>
        public static void SetClientBuild()
        {
            NetCodeClientSettings.instance.ClientTarget = NetCodeClientTarget.Client;
            NetCodeClientSettings.instance.Save();
        }
    }
}
#endif
