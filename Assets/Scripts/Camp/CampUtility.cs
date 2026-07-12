using System.Runtime.CompilerServices;

/// <summary>
/// 集中定义阵营之间的敌我关系判断
/// </summary>
public static class CampUtility
{
    /// <summary>
    /// 判断两个阵营是否应按敌对关系处理
    /// </summary>
    /// <param name="firstCamp">第一个阵营</param>
    /// <param name="secondCamp">第二个阵营</param>
    /// <returns>阵营值不同时返回真</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnemy(CampType firstCamp, CampType secondCamp)
    {
        return firstCamp != secondCamp;
    }

    /// <summary>
    /// 判断两个阵营是否应按友方关系处理
    /// </summary>
    /// <param name="firstCamp">第一个阵营</param>
    /// <param name="secondCamp">第二个阵营</param>
    /// <returns>阵营值相同时返回真</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlly(CampType firstCamp, CampType secondCamp)
    {
        return firstCamp == secondCamp;
    }
}
