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

    public static void SetLoggedInUser(string userName)
    {
        CurrentUserName = userName;
    }

    public static void Logout()
    {
        CurrentUserName = null;
    }
}
