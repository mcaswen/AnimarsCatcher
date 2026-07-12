using System.Runtime.CompilerServices;

/// <summary>
/// 集中定义阵营之间的敌我关系判断
/// </summary>
public static class CampUtility
{
    /// <summary>
    /// 判断两个阵营是否应按敌对关系处理
    /// </summary>
    /// <param name="a">第一个阵营</param>
    /// <param name="b">第二个阵营</param>
    /// <returns>阵营值不同时返回真</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnemy(CampType a, CampType b)
    {
        return a != b;
    }

    /// <summary>
    /// 判断两个阵营是否应按友方关系处理
    /// </summary>
    /// <param name="a">第一个阵营</param>
    /// <param name="b">第二个阵营</param>
    /// <returns>阵营值相同时返回真</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlly(CampType a, CampType b)
    {
        return a == b;
    }
}
