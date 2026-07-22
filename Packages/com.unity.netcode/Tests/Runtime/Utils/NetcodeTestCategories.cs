namespace Unity.NetCode.Tests
{
    internal static class NetcodeTestCategories
    {
        internal const string Smoke = "Smoke"; // 此类测试应保持很少，最多约十二个，用于快速发现框架级重大问题
        internal const string Foundational = "Foundational"; // 此类测试未通过时无需继续运行其他测试
    }
}
