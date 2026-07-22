#if USING_UNITY_LOGGING
using Unity.Logging;
using Unity.Logging.Sinks;

namespace Unity.NetCode.Tests
{
    internal static class LoggingForward
    {
        /// <summary>
        /// 将日志转发到 Unity DebugLog Sink，确保测试中的错误会实际导致测试失败
        /// 测试框架默认不会把 Logging 包的错误识别为测试错误
        /// </summary>
        public static void ForwardUnityLoggingToDebugLog()
        {
            static void AddUnityDebugLogSink(Unity.Logging.Logger logger)
            {
                // 由于无法禁用 Logger Sink，此处通过调整日志级别实现近似效果
                logger.GetOrCreateSink<UnityDebugLogSink>(new UnityDebugLogSink.Configuration(logger.Config.WriteTo, LogFormatterText.Formatter,
                    minLevelOverride: logger.MinimalLogLevelAcrossAllSystems, outputTemplateOverride: "{Message}"));
                logger.GetSink<StdOutSinkSystem>()?.SetMinimalLogLevel(LogLevel.Fatal);
                logger.GetSink<UnityEditorConsoleSink>()?.SetMinimalLogLevel(LogLevel.Fatal);
            }

            Unity.Logging.Internal.LoggerManager.OnNewLoggerCreated(AddUnityDebugLogSink);
            Unity.Logging.Internal.LoggerManager.CallForEveryLogger(AddUnityDebugLogSink);

            // 启用 Self Log，使 Logging 内部错误通过 Debug.LogError 导致测试失败
            Unity.Logging.Internal.Debug.SelfLog.SetMode(Unity.Logging.Internal.Debug.SelfLog.Mode.EnabledInUnityEngineDebugLogError);
        }
    }
}
#endif
