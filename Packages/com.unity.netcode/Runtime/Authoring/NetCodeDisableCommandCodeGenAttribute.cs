using System;

namespace Unity.NetCode
{
    /// <summary>
    /// 此特性用于禁用实现 ICommandData 或 IRpcCommand 的结构体的代码生成
    /// </summary>
    [AttributeUsage(AttributeTargets.Class|AttributeTargets.Struct)]
    public class NetCodeDisableCommandCodeGenAttribute : Attribute
    {
    }
}
