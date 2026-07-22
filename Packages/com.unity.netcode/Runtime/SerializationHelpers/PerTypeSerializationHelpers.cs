using System;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine.Assertions;

namespace SerializationHelpers
{
    /// <summary>
    /// 非基本类型使用的多种序列化辅助方法，可在模板中用于序列化这些特定类型
    /// </summary>
    public static class PerTypeSerializationHelpers
    {
        #region NetworkEndpoint
        /// <summary>
        /// 使用已打包的 DataStreamWriter 方法逐位序列化 NetworkEndpoint
        /// 支持写入已经打包的数据流，例如 Snapshot 序列化流或打包 Command
        /// </summary>
        /// <param name="value">要序列化的值</param>
        /// <param name="writer">模板 Serialize 方法提供的 Writer</param>
        public static void SerializeNetworkEndpointPacked(NetworkEndpoint value, ref DataStreamWriter writer)
        {
            Assert.IsTrue((uint)value.Family <= 255); // 防御性检查，避免 Transport 修改其枚举后超出范围
            writer.WriteRawBits((uint)value.Family, 8);
            if (value.Family != NetworkFamily.Invalid)
            {
                var adrBytes = value.GetRawAddressBytes();
                for (int i = 0; i < adrBytes.Length; i++)
                {
                    // 根据地址族向数据流写入可变长度数据，IPv4 为 4 字节，IPv6 为 16 字节，自定义类型为 60 字节
                    writer.WriteRawBits(adrBytes[i], 8);
                }

                if (value.Family == NetworkFamily.Ipv4 || value.Family == NetworkFamily.Ipv6)
                    writer.WriteRawBits(value.Port, 16);
            }
        }

        /// <summary>
        /// 与 <see cref="SerializeNetworkEndpointPacked"/> 方法对称的反序列化方法
        /// </summary>
        /// <param name="reader">模板 Deserialize 方法提供的 Reader</param>
        /// <returns>从 Reader 读取的 NetworkEndpoint</returns>
        public static NetworkEndpoint DeserializeNetworkEndpointPacked(ref DataStreamReader reader)
        {
            NetworkEndpoint value = default;
            value.Family = (NetworkFamily)reader.ReadRawBits(8);
            if (value.Family != NetworkFamily.Invalid)
            {
                var adrBytes = new NativeArray<byte>(value.Length, Allocator.Temp); // 根据地址族读取由 Length 动态确定的可变长度数据，IPv4 为 4 字节，IPv6 为 16 字节，自定义类型为 60 字节
                for (int i = 0; i < value.Length; i++)
                {
                    adrBytes[i] = (byte)reader.ReadRawBits(8);
                }

                value.SetRawAddressBytes(adrBytes, value.Family);
                if (value.Family == NetworkFamily.Ipv4 || value.Family == NetworkFamily.Ipv6)
                    value.Port = (ushort)reader.ReadRawBits(16);
            }

            return value;
        }

        /// <summary>
        /// 使用未打包且按字节对齐的 DataStreamWriter 方法序列化 NetworkEndpoint
        /// 支持写入尚未打包的数据流，例如 RPC 序列化流
        /// 注意：只能用于未打包的数据流
        /// </summary>
        /// <param name="value">要序列化的值</param>
        /// <param name="writer">模板 Serialize 方法提供的 Writer</param>
        public static void SerializeNetworkEndpointUnpacked(NetworkEndpoint value, ref DataStreamWriter writer)
        {
            writer.WriteByte((byte)value.Family);
            if (value.Family != NetworkFamily.Invalid)
            {
                writer.WriteBytes(value.GetRawAddressBytes()); // 根据地址族写入可变长度数据，IPv4 为 4 字节，IPv6 为 16 字节，自定义类型为 60 字节
                if (value.Family == NetworkFamily.Ipv4 || value.Family == NetworkFamily.Ipv6)
                    writer.WriteUShort(value.Port);
            }
        }

        /// <summary>
        /// 与 <see cref="SerializeNetworkEndpointUnpacked"/> 方法对称的反序列化方法
        /// </summary>
        /// <param name="reader">模板 Deserialize 方法提供的 Reader</param>
        /// <returns>从 Reader 读取的 NetworkEndpoint</returns>
        public static NetworkEndpoint DeserializeNetworkEndpointUnpacked(ref DataStreamReader reader)
        {
            NetworkEndpoint value = default;
            value.Family = (NetworkFamily)reader.ReadByte();
            if (value.Family != NetworkFamily.Invalid)
            {
                var adrBytes = new NativeArray<byte>(value.Length, Allocator.Temp); // 根据地址族读取由 Length 动态确定的可变长度数据，IPv4 为 4 字节，IPv6 为 16 字节，自定义类型为 60 字节
                reader.ReadBytes(adrBytes);
                value.SetRawAddressBytes(adrBytes, value.Family);
                if (value.Family == NetworkFamily.Ipv4 || value.Family == NetworkFamily.Ipv6)
                    value.Port = reader.ReadUShort();
            }

            return value;
        }
        #endregion
    }
}
