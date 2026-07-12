using System;

/// <summary>提供大小写不敏感的进程启动参数查询</summary>
public static class CommandLineManager
{
    /// <summary>判断当前进程是否包含指定启动参数</summary>
    /// <param name="flag">待查询参数</param>
    /// <returns>参数是否存在</returns>
    public static bool HasArgument(string flag)
    {
        var arguments = Environment.GetCommandLineArgs();

        for (int i = 0; i < arguments.Length; i++)
            if (string.Equals(arguments[i], flag, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
