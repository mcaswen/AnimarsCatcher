using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    public enum GenTypeKind
    {
        Invalid,
        Primitive,
        Enum,
        Struct,
        FixedList,
        FixedSizeArray
    }

    public enum ComponentType
    {
        Unknown = 0,
        Component,
        HybridComponent,
        Buffer,
        Rpc,
        CommandData,
        Input
    }

    // 该类型供 Source Generator 内部使用，但必须与运行时 NetCode 类型保持同步
    // 对应 Runtime/Authoring/GhostComponentAttribute.cs
    internal class GhostComponentAttribute
    {
        public GhostPrefabType PrefabType;
        public GhostSendType SendTypeOptimization;
        public SendToOwnerType OwnerSendType;
        public bool SendDataForChildEntity;

        public GhostComponentAttribute()
        {
            PrefabType = GhostPrefabType.All;
            SendTypeOptimization = GhostSendType.AllClients;
            OwnerSendType = SendToOwnerType.All;
            SendDataForChildEntity = false;
        }
    }

    /// <summary>
    /// 完全独立于 Roslyn 类型的类型描述信息
    /// 用于为 Ghost 与 Command 生成序列化代码
    /// </summary>
    internal class TypeInformation
    {
#pragma warning disable 649
        public string Namespace;
        public string TypeFullName;
        // 仅对 Enum 等具有不同底层类型的类型有效，其他情况为空
        public string UnderlyingTypeName;
        // 仅对字段有效，其他情况为空或 null
        public string FieldName;
        // 可选且仅对字段有效，其他情况为空或 null
        // 当访问模式不符合自动规则时，用于保存访问 Snapshot 数据字段的替代路径或名称
        // 例如 parent.field.name -> parent_field_name
        public string SnapshotFieldName;
        // 仅对字段有效，其他情况为空或 null
        public string FieldTypeName;
        // 仅对字段有效，其他情况为空或 null
        public string ContainingTypeFullName;
        public GenTypeKind Kind;
        // 仅对根类型有效，成员始终为 NotApplicable
        public ComponentType ComponentType;
        // 掩码允许时子节点可以继承并设置特性，默认允许全部
        public TypeAttribute.AttributeFlags AttributeMask = TypeAttribute.AttributeFlags.All;
        public TypeAttribute Attribute;
        // 仅适用于根类型
        public GhostComponentAttribute GhostAttribute;
        // 从根节点开始的字段路径
        public string FieldPath;
        public ITypeSymbol Symbol;
#pragma warning restore 649
        // 类型在语法树中的 TextSpan 位置
        public Location Location;
        // 仅对泛型类型有效
        public string GenericTypeName;
        public TypeInformation PointeeType;
        public List<TypeInformation> GhostFields = new List<TypeInformation>();
        public bool ShouldSerializeEnabledBit;
        public bool HasDontSupportPrefabOverridesAttribute;
        public bool IsTestVariant;
        public bool CanBatchPredict;
        // 固定 Buffer 与 FixedList 的元素数量
        public int ElementCount;
        public TypeDescription Description
        {
            get
            {
                var description = new TypeDescription
                {
                    TypeFullName = TypeFullName,
                    Attribute = Attribute
                };
                if (Kind == GenTypeKind.Enum)
                    description.Key = UnderlyingTypeName;
                else if (Kind == GenTypeKind.FixedList)
                    description.Key = GenericTypeName;
                else
                    description.Key = TypeFullName;
                return description;
            }
        }

        public bool IsValid => Kind != GenTypeKind.Invalid;

        public override string ToString()
        {
            return $"{TypeFullName} (quantized={Attribute.quantization} composite={Attribute.aggregateChangeMask} smoothing={Attribute.smoothing} subtype={Attribute.subtype})";
        }
    }
}
