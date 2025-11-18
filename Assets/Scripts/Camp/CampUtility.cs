using System.Runtime.CompilerServices;

public static class CampUtility
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnemy(CampType a, CampType b)
    {
        return a != b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlly(CampType a, CampType b)
    {
        return a == b;
    }
}
