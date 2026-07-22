#if USING_OBSOLETE_METHODS_VIA_INTERNALSVISIBLETO
#pragma warning disable 0436
#endif
#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
#if USING_UNITY_LOGGING
using Logger = Unity.Logging.Logger;
using Unity.Logging;
using Unity.Logging.Internal;
using Unity.Logging.Sinks;
#endif

namespace Unity.NetCode
{
    /// <summary>
    /// 把此组件添加到任意连接 Entity，即具有 <see cref="NetworkStreamConnection"/> 组件的 Entity，
    /// 以启用详细的 NetCode 数据包转储日志
    /// </summary>
    /// <remarks>
    /// 可以通过 PlayMode Tools Window 为全部连接全局启用数据包转储
    /// 也可以通过 `NetCodeDebugConfigAuthoring` 向任意 SubScene 添加 <see cref="NetCodeDebugConfig"/>，
    /// 并把 <see cref="NetCodeDebugConfig.DumpPackets"/> 标志设为 true
    /// </remarks>
    public struct EnablePacketLogging : IComponentData
    {
#if NETCODE_DEBUG
        internal PacketDumpLogger NetDebugPacketCache;

        /// <summary>
        /// 使用前检查并确保数据包缓存已经创建
        /// </summary>
        public bool IsPacketCacheCreated => NetDebugPacketCache.IsCreated;

        /// <summary>
        /// 向 NetCode 的逐连接数据包转储添加自定义日志
        /// </summary>
        /// <remarks>出于安全考虑，必须以写入权限获取此组件</remarks>
        /// <param name="msg">要追加的消息，不会自动添加换行符</param>
        public void LogToPacket(in FixedString512Bytes msg)
        {
            if (!NetDebugPacketCache.IsCreated)
                throw new InvalidOperationException("LogToPacket failed as cache has not been created yet! Wait for InitAndFetch to be called via netcode's GhostSend/ReceiveSystem.");
            NetDebugPacketCache.Log(msg);
        }
#endif

#if NETCODE_DEBUG
        /// <summary>
        /// NetDebugPacket 是由其他系统维护生命周期的结构体
        /// 此方法获取它是否启用，并在此过程中确保缓存 <see cref="NetDebugPacketCache"/> 已设置
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="lookup"></param>
        /// <param name="netDebugPacket"></param>
        /// <returns>Entity 具有 EnablePacketLogging 组件时返回 1</returns>
        /// <exception cref="InvalidOperationException"></exception>
        internal static byte InitAndFetch(Entity entity, ComponentLookup<EnablePacketLogging> lookup, in PacketDumpLogger netDebugPacket)
        {
            var componentRef = lookup.GetRefRWOptional(entity);
            if (!componentRef.IsValid)
                return 0;
            if (!netDebugPacket.IsCreated)
                throw new InvalidOperationException("Packet logger has not been setup, InitAndFetch failed! Aborting.");
            if (!componentRef.ValueRO.NetDebugPacketCache.IsCreated)
                componentRef.ValueRW.NetDebugPacketCache = netDebugPacket;
            return 1;
        }
#endif
    }

    /// <summary>
    /// 把断开原因错误码转换为便于阅读的错误消息
    /// </summary>
    [Obsolete("Use ToFixedString extension methods. (RemovedAfter Entities 2.0)", false)]
    public struct DisconnectReasonEnumToString
    {
        /// <summary>
        /// 把错误码转换为便于阅读的错误消息
        /// </summary>
        /// <param name="index">断开连接的错误原因</param>
        /// <returns>
        /// 包含错误消息的字符串
        /// </returns>
        public static FixedString32Bytes Convert(int index)
        {
            return ((NetworkStreamDisconnectReason) index).ToFixedString();
        }
    }

    /// <summary>
    /// 枚举的 ToFixedString 工具方法
    /// </summary>
    public static class NetCodeUtils
    {
        /// <summary>
        /// 返回 FixedString 格式的枚举值名称
        /// </summary>
        /// <param name="reason">源枚举</param>
        /// <returns>FixedString 格式的枚举值名称</returns>
        public static FixedString32Bytes ToFixedString(this NetworkStreamDisconnectReason reason)
        {
            switch (reason)
            {
                case NetworkStreamDisconnectReason.ConnectionClose: return nameof(NetworkStreamDisconnectReason.ConnectionClose);
                case NetworkStreamDisconnectReason.Timeout: return nameof(NetworkStreamDisconnectReason.Timeout);
                case NetworkStreamDisconnectReason.MaxConnectionAttempts: return nameof(NetworkStreamDisconnectReason.MaxConnectionAttempts);
                case NetworkStreamDisconnectReason.ClosedByRemote: return nameof(NetworkStreamDisconnectReason.ClosedByRemote);
                case NetworkStreamDisconnectReason.BadProtocolVersion: return nameof(NetworkStreamDisconnectReason.BadProtocolVersion);
                case NetworkStreamDisconnectReason.InvalidRpc: return nameof(NetworkStreamDisconnectReason.InvalidRpc);
                case NetworkStreamDisconnectReason.AuthenticationFailure: return nameof(NetworkStreamDisconnectReason.AuthenticationFailure);
                case NetworkStreamDisconnectReason.ProtocolError: return nameof(NetworkStreamDisconnectReason.ProtocolError);
                default: return $"DisconnectReason_{(int) reason}";
            }
        }


        // TODO 此方法一直用于设置连接状态，因此实际上不属于 NetDebug
        /// <summary>
        /// 把 Transport 状态转换为 NetCode 状态
        /// </summary>
        /// <param name="transportState">源枚举</param>
        /// <param name="hasHandshaked">Handshake 流程已完成时为 true</param>
        /// <param name="hasApproval">已经获批且启用 Approval 流程，或无需 Approval 时为 true</param>
        /// <returns>NetCode 连接状态</returns>
        /// <exception cref="ArgumentOutOfRangeException">Transport 状态未知时抛出</exception>
        public static ConnectionState.State ToNetcodeState(this NetworkConnection.State transportState, bool hasHandshaked, bool hasApproval = true)
        {
            switch (transportState)
            {
                // 参见文档
                case NetworkConnection.State.Connected:
                    if (Hint.Likely(hasHandshaked && hasApproval))
                        return ConnectionState.State.Connected;
                    return hasHandshaked ? ConnectionState.State.Approval : ConnectionState.State.Handshake;
                case NetworkConnection.State.Disconnected: return ConnectionState.State.Disconnected;
                case NetworkConnection.State.Disconnecting: return ConnectionState.State.Disconnected;
                case NetworkConnection.State.Connecting: return ConnectionState.State.Connecting;
                default:
                    throw new ArgumentOutOfRangeException(nameof(transportState), transportState, nameof(ToNetcodeState));
            }
        }

        /// <summary>
        /// 返回 FixedString 格式的枚举值名称
        /// </summary>
        /// <param name="state">源枚举</param>
        /// <returns>FixedString 格式的枚举值名称</returns>
        public static FixedString32Bytes ToFixedString(this ConnectionState.State state)
        {
            switch (state)
            {
                case ConnectionState.State.Unknown: return nameof(ConnectionState.State.Unknown);
                case ConnectionState.State.Disconnected: return nameof(ConnectionState.State.Disconnected);
                case ConnectionState.State.Connecting: return nameof(ConnectionState.State.Connecting);
                case ConnectionState.State.Handshake: return nameof(ConnectionState.State.Handshake);
                case ConnectionState.State.Approval: return nameof(ConnectionState.State.Approval);
                case ConnectionState.State.Connected: return nameof(ConnectionState.State.Connected);
                default: return $"ConnectionState_{(int) state}";
            }
        }

        /// <summary>
        /// 返回 FixedString 格式的枚举值名称
        /// </summary>
        /// <param name="state">源枚举</param>
        /// <returns>FixedString 格式的枚举值名称</returns>
        public static FixedString32Bytes ToFixedString(this NetworkConnection.State state)
        {
            switch (state)
            {
                case NetworkConnection.State.Disconnected: return nameof(NetworkConnection.State.Disconnected);
                case NetworkConnection.State.Disconnecting: return nameof(NetworkConnection.State.Disconnecting);
                case NetworkConnection.State.Connecting: return nameof(NetworkConnection.State.Connecting);
                case NetworkConnection.State.Connected: return nameof(NetworkConnection.State.Connected);
                default: return $"NetworkConnection.State_{(int) state}";
            }
        }
    }

    /// <summary>
    /// 处理 NetCode 日志记录与日志管理的 Singleton
    /// </summary>
    public struct NetDebug : IComponentData
    {
        internal const LogLevelType DefaultLogLevel = LogLevelType.Notify;

        /// <summary>
        /// 使用此方法获取保存 NetCode 日志文件的平台专用文件夹
        /// 桌面平台使用 <see cref="UnityEngine.Application.consoleLogPath"/>
        /// 移动平台使用 <see cref="UnityEngine.Application.persistentDataPath"/>
        /// DOTS Runtime 构建可以通过 -logfile 命令行开关自定义输出位置
        ///
        /// 无论哪种情况，如果日志路径为 null 或空，则改用当前目录下的 Logs 文件夹
        /// </summary>
        /// <returns>包含日志文件夹完整路径的字符串</returns>
        public static string LogFolderForPlatform()
        {
#if UNITY_ANDROID || UNITY_IOS
            var persistentLogPath = UnityEngine.Application.persistentDataPath;
            if (!string.IsNullOrEmpty(persistentLogPath))
                return persistentLogPath;
#else
            // 默认把日志输出到 Player 和 Console 输出所在的位置
            var consoleLogPath = UnityEngine.Application.consoleLogPath;
            if (!string.IsNullOrEmpty(consoleLogPath))
                return Path.GetDirectoryName(UnityEngine.Application.consoleLogPath);
#endif
            return "Logs";
        }

        // TODO Logging 默认应已为此用途提供合适的文件夹
        internal static FixedString512Bytes GetAndCreateLogFolder()
        {
            var logPath = LogFolderForPlatform();
            if (!Directory.Exists(logPath))
                Directory.CreateDirectory(logPath);
            return logPath;
        }

        private LogLevelType m_LogLevel;

#if NETCODE_DEBUG
        internal NativeHashMap<int, FixedString128Bytes>.ReadOnly ComponentTypeNameLookup;
#endif

#if USING_UNITY_LOGGING
        private LogLevel m_CurrentLogLevel;
        private LoggerHandle m_LoggerHandle;

        private Logger GetOrCreateLogger()
        {
            Logger logger = null;
            if (m_LoggerHandle.IsValid)
                logger = LoggerManager.GetLogger(m_LoggerHandle);

            if (logger == null)
            {
                logger = new LoggerConfig()
                    .MinimumLevel.Set(m_CurrentLogLevel)
                    .CaptureStacktrace(false)
                    .RedirectUnityLogs(false)
                    // 使用与当前 Unity Logging 兼容的正确格式
                    .WriteTo.UnityDebugLog(minLevel: m_CurrentLogLevel, outputTemplate: new FixedString512Bytes("{Message}"))
                    .CreateLogger();
                m_LoggerHandle = logger.Handle;
            }

            return logger;
        }
#endif
        private void SetLoggerLevel(LogLevelType newLevel)
        {
#if USING_UNITY_LOGGING
            m_CurrentLogLevel = newLevel switch
            {
                LogLevelType.Debug => Logging.LogLevel.Debug,
                LogLevelType.Notify => Logging.LogLevel.Info,
                LogLevelType.Warning => Logging.LogLevel.Warning,
                LogLevelType.Error => Logging.LogLevel.Error,
                LogLevelType.Exception => Logging.LogLevel.Fatal,
                _ => throw new ArgumentOutOfRangeException()
            };

            var logger = GetOrCreateLogger();
            logger.SetMinimalLogLevelAcrossAllSinks(m_CurrentLogLevel);
#endif
        }
        internal void Initialize()
        {
            MaxRpcAgeFrames = 4;
            LogLevel = DefaultLogLevel;
            // 默认抑制此警告，因为它会在测试中造成大量误报
            SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning = true;

            WarnBatchedTicks = true;
            WarnBatchedTicksRollingWindowSize = 4;
            WarnAboveAverageBatchedTicksPerFrame = 1.2f;
        }

        /// <summary>
        /// 销毁调试 Logger 分配的内部资源，并刷新所有待处理消息
        /// </summary>
        public void Dispose()
        {
#if USING_UNITY_LOGGING
            if (!m_LoggerHandle.IsValid)
                return;
            var logger = LoggerManager.GetLogger(m_LoggerHandle);
            logger?.Dispose();

            m_LoggerHandle = default;
#endif
        }

        /// <summary>
        /// 如果禁用 <see cref="UnityEngine.Application.runInBackground"/>，用户切出游戏或游戏失去焦点时会发生客户端断线
        /// 因此强烈建议在 `Project Settings... Player... Resolution and Presentation... Run In Background` 中启用 Run in Background
        /// </summary>
        /// <remarks>
        /// 把 <see cref="SuppressApplicationRunInBackgroundWarning"/> 设为 true，
        /// 可以关闭 Run in Background 而不触发建议日志
        /// </remarks>
        [field: MarshalAs(UnmanagedType.U1)]
        public bool SuppressApplicationRunInBackgroundWarning { get; set; }

        /// <summary>
        /// 调试时，把“Approval 禁用时发送 <see cref="IApprovalRpcCommand"/> RPC”视为警告会很有帮助
        /// 但也可能使用 Approval RPC 发送加入对局信息，此时可以把该值设为 true 以抑制警告
        /// 默认启用日志抑制，把此标志设为 false 才会显示警告
        /// </summary>
        [field: MarshalAs(UnmanagedType.U1)]
        public bool SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning { get; set; }

        /// <summary>
        /// 防止 <see cref="SuppressApplicationRunInBackgroundWarning"/> 产生重复日志
        /// </summary>
        [field: MarshalAs(UnmanagedType.U1)]
        internal bool HasWarnedAboutApplicationRunInBackground { get; set; }

        /// <summary>
        ///     如果 NetCode RPC 经过指定数量的模拟帧后仍未消费或销毁，即仍未处理，则触发警告
        ///     该数量包含边界值，参见 <see cref="ReceiveRpcCommandRequest.Age" />
        ///     设为 0 可禁用
        /// </summary>
        public ushort MaxRpcAgeFrames { get; set; }

        // 当帧时间使 Fixed Update 无法追上模拟时间时，系统会批处理 Tick
        // 每帧不再执行 N 个 fixedTime Tick，而是执行 M 个时长为 (N/M)*fixedTime 的 Tick
        // 这样可以让模拟追上进度，但会降低插值性能并可能引入预测误差，因为服务器模拟帧数少于客户端预测帧数，需要进行调整
        // 这种情况在编辑器和性能较差时很常见；如果插值良好且不频繁发生，视觉影响应很小
        // 如果每帧都发生，性能会严重下降

        /// <summary>
        ///     Tick 被批处理时显示警告
        /// </summary>
        /// <remarks>
        ///    当帧时间使 Fixed Update 无法追上模拟时间时会显示警告
        ///    系统会批处理 Tick，每帧不再执行 N 个 fixedTime Tick，而是执行 M 个时长为 (N/M)*fixedTime 的 Tick
        ///    这样可以让模拟追上进度，但会降低插值性能并可能引入预测误差，因为服务器模拟帧数少于客户端预测帧数，需要进行调整
        ///    这种情况在编辑器和性能较差时很常见；如果插值良好且不频繁发生，视觉影响应很小
        ///    如果每帧都发生，性能会严重下降
        /// </remarks>
        [field: MarshalAs(UnmanagedType.U1)]
        public bool WarnBatchedTicks;

        /// <summary>
        ///     计算包含 Tick Batching 的每帧 Tick 数量平均值时使用的滚动窗口大小
        /// </summary>
        public int WarnBatchedTicksRollingWindowSize;

        /// <summary>
        ///     每帧平均 Tick 数量高于此值时显示警告
        /// </summary>
        public float WarnAboveAverageBatchedTicksPerFrame;

        /// <summary>
        /// 当前调试日志级别，默认值为 <see cref="LogLevelType.Notify"/>
        /// </summary>
        [ExcludeFromBurstCompatTesting("may use managed objects")]
        public LogLevelType LogLevel
        {
            set
            {
                m_LogLevel = value;

                SetLoggerLevel(m_LogLevel);
            }
            get => m_LogLevel;
        }

        /// <summary>
        /// 可用的 NetCode 日志级别，默认值为 <see cref="Notify"/>
        /// 使用 <see cref="NetCodeDebugConfig"/> 组件配置日志级别
        /// </summary>
        public enum LogLevelType
        {
            /// <summary>
            /// Debug 级别，输出最详细，仅调试消息应使用此级别
            /// </summary>
            Debug = 1,
            /// <summary>
            /// 默认调试级别
            /// 包含有用信息、不会重复刷屏且没有可测量性能影响的消息可以使用此级别
            /// </summary>
            Notify = 2,
            /// <summary>
            /// 用于非严重错误或潜在问题的级别
            /// </summary>
            Warning = 3,
            /// <summary>
            /// 用于全部错误消息的级别，无论是否严重
            /// </summary>
            Error = 4,
            /// <summary>
            /// 设置后只输出异常
            /// </summary>
            Exception = 5,
        }

        /// <summary>
        /// 以 Debug 级别输出日志消息
        /// </summary>
        /// <param name="msg">ASCII 消息字符串，不支持 Unicode</param>
        public readonly void DebugLog(in FixedString512Bytes msg)
        {
#if USING_UNITY_LOGGING
            Unity.Logging.Log.To(m_LoggerHandle).Debug(msg);
#else
            if(m_LogLevel <= LogLevelType.Debug)
                UnityEngine.Debug.Log(msg);
#endif
        }

        /// <summary>
        /// 以 Notify 级别输出日志消息
        /// </summary>
        /// <param name="msg">ASCII 消息字符串，不支持 Unicode</param>
        public readonly void Log(in FixedString512Bytes msg)
        {
#if USING_UNITY_LOGGING
            Unity.Logging.Log.To(m_LoggerHandle).Info(msg);
#else
            if(m_LogLevel <= LogLevelType.Notify)
                UnityEngine.Debug.Log(msg);
#endif
        }

        /// <summary>
        /// 以 Warning 级别输出日志消息
        /// </summary>
        /// <param name="msg">ASCII 消息字符串，不支持 Unicode</param>
        public readonly void LogWarning(in FixedString512Bytes msg)
        {
#if USING_UNITY_LOGGING
            Unity.Logging.Log.To(m_LoggerHandle).Warning(msg);
#else
            if(m_LogLevel <= LogLevelType.Warning)
                UnityEngine.Debug.LogWarning(msg);
#endif
        }

        /// <summary>
        /// 以 Error 级别输出日志消息
        /// </summary>
        /// <param name="msg">ASCII 消息字符串，不支持 Unicode</param>
        public readonly void LogError(in FixedString512Bytes msg)
        {
#if USING_UNITY_LOGGING
            Unity.Logging.Log.To(m_LoggerHandle).Error(msg);
#else
            if(m_LogLevel <= LogLevelType.Error)
                UnityEngine.Debug.LogError(msg);
#endif
        }

        /// <summary>
        /// 把无符号整数 Bitmask 输出为字符串的工具方法
        /// 会跳过首个置位 bit 之前的全部最高有效位零
        /// 例如：
        /// mask: 00010 0001 0000 0010
        /// 输出为 "10000100000010"
        /// </summary>
        /// <param name="mask">要输出的 Bitmask</param>
        /// <returns></returns>
        internal static FixedString64Bytes PrintMask(uint mask)
        {
            FixedString64Bytes maskString = default;
            for (int i = 0; i < 32; ++i)
            {
                var bit = (mask>>31)&1;
                mask <<= 1;
                if (maskString.Length == 0 && bit == 0)
                    continue;
                maskString.Append(bit);
            }

            if (maskString.Length == 0)
                maskString = "0";
            return maskString;
        }

        /// <summary>
        /// 以十六进制格式输出无符号长整数
        /// </summary>
        /// <param name="value">要转换的整数</param>
        /// <param name="bitSize">要输出的 bit 数量，必须是 4 的倍数</param>
        /// <returns></returns>
        internal static FixedString32Bytes PrintHex(ulong value, int bitSize)
        {
            FixedString32Bytes temp = new FixedString32Bytes();
            temp.Add((byte)'0');
            temp.Add((byte)'x');
            if (value == 0)
            {
                temp.Add((byte)'0');
                return temp;
            }
            int i = bitSize;
            do
            {
                i -= 4;
                int nibble = (int) (value >> i) & 0xF;
                if(nibble == 0 && temp.Length == 2)
                    continue;
                nibble += (nibble >= 10) ? 'A' - 10 : '0';
                temp.Add((byte)nibble);
            } while (i > 0);
            return temp;
        }
        /// <summary>
        /// 以十六进制格式输出无符号整数
        /// </summary>
        /// <param name="value">要转换的无符号值</param>
        /// <returns>十六进制格式的无符号整数</returns>
        public static FixedString32Bytes PrintHex(uint value)
        {
            return PrintHex(value, 32);
        }
        /// <summary>
        /// 以十六进制格式输出无符号长整数
        /// </summary>
        /// <param name="value">要转换的无符号值</param>
        /// <returns>十六进制格式的无符号长整数</returns>
        public static FixedString32Bytes PrintHex(ulong value)
        {
            return PrintHex(value, 64);
        }
    }
}

#if USING_OBSOLETE_METHODS_VIA_INTERNALSVISIBLETO
#pragma warning restore 0436
#endif
