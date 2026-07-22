using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 编辑器和运行时用于计算及检查 Ghost 组件变体哈希值的工具集合
    /// </summary>
    public static class GhostVariantsUtility
    {
        internal const string k_DefaultVariantName = "Default";
        internal const string k_ClientOnlyVariant = nameof(ClientOnlyVariant);
        internal const string k_ServerOnlyVariant = nameof(ServerOnlyVariant);
        internal const string k_DontSerializeVariant = nameof(DontSerializeVariant);
        static readonly FixedString32Bytes k_NetCodeGhostNetVariant = "NetCode.GhostNetVariant";
        static readonly ulong k_NetCodeGhostNetVariantHash = TypeHash.FNV1A64(k_NetCodeGhostNetVariant);

        internal static readonly ulong ClientOnlyHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_ClientOnlyVariant}"));
        internal static readonly ulong ServerOnlyHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_ServerOnlyVariant}"));
        internal static readonly ulong DontSerializeHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_DontSerializeVariant}"));

        static ulong CalculateVariantHash(ulong variantTypeHash, ulong componentTypeHash)
        {
            var hash = k_NetCodeGhostNetVariantHash;
            hash = TypeHash.CombineFNV1A64(hash, componentTypeHash);
            hash = TypeHash.CombineFNV1A64(hash, variantTypeHash);
            return hash;
        }
        /// <summary>
        /// 计算组件类型自身的变体哈希值，以便获取元数据
        /// </summary>
        /// <remarks>这种设计有些特殊：组件的默认序列化器就是 ComponentType 本身，即组件自身也是一个变体</remarks>
        /// <param name="componentType">同时用作组件和变体的 ComponentType</param>
        /// <returns>计算出的哈希值</returns>
        public static ulong CalculateVariantHashForComponent(ComponentType componentType)
        {
            var componentTypeHash = TypeManager.GetFullNameHash(componentType.TypeIndex);
            return CalculateVariantHash(componentTypeHash, componentTypeHash);
        }

        /// <summary>

        /// 通过 <see cref="TypeManager.GetTypeNameFixed"/> 为变体计算稳定哈希值

        /// </summary>
        /// <param name="variantTypeFullName">变体类型的 <see cref="Type.FullName"/></param>
        /// <param name="componentType">此变体所应用的 ComponentType</param>
        /// <returns>计算出的哈希值</returns>
        public static ulong UncheckedVariantHash(in FixedString512Bytes variantTypeFullName, ComponentType componentType)
        {
            return CalculateVariantHash(TypeHash.FNV1A64(variantTypeFullName), TypeManager.GetFullNameHash(componentType.TypeIndex));
        }

        /// <summary>

        /// 为变体与组件配对计算变体哈希值

        /// </summary>
        /// <param name="variantTypeFullName">变体类型的 System.Type.FullName</param>
        /// <param name="componentTypeFullName">此变体所应用组件类型的 System.Type.FullName</param>
        /// <returns>计算出的哈希值</returns>
        public static ulong UncheckedVariantHash(in FixedString512Bytes variantTypeFullName, in FixedString512Bytes componentTypeFullName)
        {
            return CalculateVariantHash(TypeHash.FNV1A64(variantTypeFullName), TypeHash.FNV1A64(componentTypeFullName));
        }

        /// <summary>
        /// 为变体与组件配对计算变体哈希值，此版本不兼容 Burst
        /// </summary>
        /// <param name="variantTypeFullName">变体类型的 System.Type.FullName</param>
        /// <param name="componentTypeFullName">此变体所应用组件类型的 System.Type.FullName</param>
        /// <returns>计算出的哈希值</returns>
        /// <remarks>此方法不兼容 Burst</remarks>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public static ulong UncheckedVariantHashNBC(string variantTypeFullName, string componentTypeFullName)
        {
            return CalculateVariantHash(TypeHash.FNV1A64(variantTypeFullName), TypeHash.FNV1A64(componentTypeFullName));
        }

        /// <summary>组合变体 Type.Fullname 与 <see cref="ComponentType"/> 名称哈希
        /// <see cref="TypeManager.GetFullNameHash"/>，为变体计算稳定哈希值</summary>
        /// <param name="variantStructDeclaration">变体结构体的声明类型</param>
        /// <param name="componentType">此变体所应用的 ComponentType</param>
        /// <returns>计算出的哈希值</returns>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public static ulong UncheckedVariantHashNBC(Type variantStructDeclaration, ComponentType componentType)
        {
            return CalculateVariantHash(TypeHash.FNV1A64(variantStructDeclaration.FullName), TypeManager.GetFullNameHash(componentType.TypeIndex));
        }

        /// <summary>

        /// 为变体与组件配对计算变体哈希值

        /// </summary>
        /// <param name="variantTypeHash">变体类型 System.Type.FullName 的哈希值</param>
        /// <param name="componentType">此变体所应用的 ComponentType</param>
        /// <returns>计算出的哈希值</returns>
        public static ulong UncheckedVariantHash(ulong variantTypeHash, ComponentType componentType)
        {
            return CalculateVariantHash(variantTypeHash, TypeManager.GetFullNameHash(componentType.TypeIndex));
        }

    }
}
