/// <summary>
/// 保存当前进程内已登录玩家的会话身份
/// 该状态不会写入磁盘 场景切换期间由静态生命周期保留
/// </summary>
public static class PlayerSession
{
    public static string CurrentUserName { get; private set; }

    public static bool IsLoggedIn
    {
        get
        {
            return !string.IsNullOrEmpty(CurrentUserName);
        }
    }

    /// <summary>
    /// 记录成功通过本地认证的用户名
    /// </summary>
    /// <param name="userName">已验证的用户名</param>
    public static void SetLoggedInUser(string userName)
    {
        CurrentUserName = userName;
    }

    /// <summary>
    /// 清除当前登录身份
    /// </summary>
    public static void Logout()
    {
        CurrentUserName = null;
    }
}
