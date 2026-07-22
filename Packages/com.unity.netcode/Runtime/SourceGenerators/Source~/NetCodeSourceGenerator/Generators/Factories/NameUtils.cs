using Microsoft.CodeAnalysis;

namespace Unity.NetCode.Generators;

internal class NameUtils
{
    // 命名要求：
    // - X.A 与 Y.A 必须生成互不冲突的代码
    // - Unity.NetCode 可以同时包含 X.A 与 Y.A，因此必须能唯一识别 A
    // - 生成文件名不能超过 Windows 路径长度限制
    // - 应便于用户代码访问
    // - 不得与子命名空间冲突，例如 Unity.NetCode.Generated.Unity.NetCode 与 Unity.NetCode.X
    // TODO: 为生成类型生成访问器供用户使用，参见示例中的 CustomChunkSerializer
    internal static void UpdateNameAndNamespace(TypeInformation typeInfo, ref CodeGenerator.Context codeGenContext, ITypeSymbol candidateSymbol)
    {
        var uniquePrefix = $"{codeGenContext.rootNs}";
        if (!string.IsNullOrEmpty(typeInfo.Namespace))
            uniquePrefix += $".{typeInfo.Namespace}";
        codeGenContext.generatedNs = $"{uniquePrefix.Replace(".", "_")}"; // 替换点号以区别于原类型命名空间，避免 C# 在生成命名空间内查找原类型
        var typeName = Roslyn.Extensions.GetTypeNameWithDeclaringTypename(candidateSymbol);
        codeGenContext.generatorName = $"{codeGenContext.generatedNs}_{typeName}";
        codeGenContext.generatedFilePrefix = $"{Utilities.TypeHash.FNV1A64(uniquePrefix).ToString()}_{typeName}";
    }
}
