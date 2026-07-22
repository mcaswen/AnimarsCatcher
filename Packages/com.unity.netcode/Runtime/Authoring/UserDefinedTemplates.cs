// 重要提示：此文件由 NetCode 源码生成器共享
// 此处不允许引用 UnityEngine、UnityEditor 或其他包的 DLL
using System.Collections.Generic;

namespace Unity.NetCode.Generators
{
    ///<summary>
    /// UserDefinedTemplates 用于向代码生成系统添加自定义模板
    /// 在引用 Unity.NetCode 的 AssemblyDefinitionReference（.asmref）中添加 partial 类定义，
    /// 并通过把新类型添加到模板列表来实现 <see cref="RegisterTemplates"/> 方法
    /// </summary>
    public static partial class UserDefinedTemplates
    {
        internal static List<TypeRegistryEntry> Templates;

        static UserDefinedTemplates()
        {
            Templates = new List<TypeRegistryEntry>();
            RegisterTemplates(Templates, "Packages/com.unity.netcode/Editor/Templates");
        }
        static partial void RegisterTemplates(List<TypeRegistryEntry> templates, string defaultRootPath);
    }
}
