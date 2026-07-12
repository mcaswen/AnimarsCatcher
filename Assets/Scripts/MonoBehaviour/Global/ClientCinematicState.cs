namespace AnimarsCatcher.Mono.Global
{
    /// <summary>
    /// 共享客户端开场演出的运行状态
    /// 供输入和 UI 系统判断是否需要暂时阻止交互
    /// </summary>
    public static class ClientCinematicState
    {
        public static bool IsRunning;
        public static bool ShouldRunIntro;
    }
}
