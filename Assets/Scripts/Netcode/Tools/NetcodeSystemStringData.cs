public enum ArgsType { Dedicated, ServerUi }

public static class NetPorts
{
    public const ushort Game = 7979;
}

public static class NetArgs
{
    public const string Dedicated = "-dedicated";
    public const string ServerUi = "-serverui";

    public static string ToString(ArgsType t)
        => t == ArgsType.Dedicated ? Dedicated : ServerUi;
}

public static class NetWorlds
{
    public const string Default = "Default";
}
