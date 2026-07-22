namespace Unity.NetCode
{
    /// <summary>
    /// 保存一组可在整个项目中使用的 int 常量，用于指定 <see cref="GhostFieldAttribute"/> 的子类型
    /// 用户可以通过指向 Unity.NetCode.Gen 的 AssemblyDefinitionReference 扩展此列表，
    /// 添加一个扩展该类型的 partial 类，并在其中加入新的常量字面量
    /// </summary>
    /// <remarks>
    /// GhostFieldSubType 不使用 enum 是因为存在一些限制，部分来自编译管线，部分来自 SourceGenerator API
    /// 首先，Microsoft SourceGenerator 只能追加内容，这意味着无法像 Analyzer 那样修改语法树、移除或添加节点
    /// 为绕过该限制，一种可能的方案是使用小型 IL 后处理器，将枚举字面量注入程序集
    /// 每次添加或移除子类型时都会重新导入 NetCode 运行时程序集，
    /// 因而原先假设 IL 后处理会在编译任何依赖 DLL 前正确修改该 DLL
    /// 实际上 Unity.NetCode.dll 确实包含正确的元数据，但 ILPostProcessorRunner 运行得更晚，
    /// 导致部分 DLL 因时序问题无法正确编译
    /// 进一步调查也许可以解决此问题，但这意味着再次与编译流程对抗，而这正是我们希望避免的
    /// 因此改用 partial 类保存整数常量，用户也可以添加新的 const 字面量
    ///
    /// 为什么使用 AssemblyDefinitionReference：通过源码生成器直接向 NetCode.dll 添加 partial 类可以正常工作，
    /// 但会失去 IDE 自动补全功能，目前没有 IDE 原生支持这种情况
    /// Visual Studio 对普通 C# 项目存在一些规避方式，例如从解决方案中移除原始文件或重启 IDE，
    /// 但 Rider 和 VSCode 的行为不同
    /// 使用 Assembly Definition Reference 原理上完成了相同工作，同时可以正常使用补全，改善用户体验
    /// </remarks>
    public static partial class GhostFieldSubType
    {
        /// <summary>
        /// <see cref="GhostFieldAttribute.SubType"/> 的默认值
        /// </summary>
        public const int None = 0;
    }
}
