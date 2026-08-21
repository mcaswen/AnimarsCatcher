using System;
using AnimarsCatcher.Gameplay.Contracts;

namespace AnimarsCatcher.Networking
{
    /// <summary>
    /// 从启动参数解析当前进程使用的 Ani 移动后端
    /// </summary>
    public static class AniMovementBackendLaunchConfiguration
    {
        private const string ArgumentName = "-movement-backend";

        public static AniMovementBackend Current => Parse(Environment.GetCommandLineArgs());

        /// <summary>
        /// 解析启动参数，未指定时继续使用 Legacy 后端
        /// </summary>
        /// <param name="arguments">进程启动参数</param>
        /// <returns>需要启用的移动后端</returns>
        public static AniMovementBackend Parse(string[] arguments)
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i];
                if (argument.StartsWith(ArgumentName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseValue(argument[(ArgumentName.Length + 1)..]);
                }

                if (string.Equals(argument, ArgumentName, StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < arguments.Length)
                {
                    return ParseValue(arguments[i + 1]);
                }
            }

            return AniMovementBackend.LegacyNavMesh;
        }

        private static AniMovementBackend ParseValue(string value)
        {
            if (string.Equals(value, "grid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "clearance-grid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "clearancegrid", StringComparison.OrdinalIgnoreCase))
            {
                return AniMovementBackend.ClearanceGrid;
            }

            if (string.Equals(value, "legacy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "legacy-navmesh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "legacynavmesh", StringComparison.OrdinalIgnoreCase))
            {
                return AniMovementBackend.LegacyNavMesh;
            }

            throw new ArgumentException(
                $"无法识别移动后端“{value}”，可用值为 grid 或 legacy",
                nameof(value));
        }
    }
}
