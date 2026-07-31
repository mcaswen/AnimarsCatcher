using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation.Harness
{
    /// <summary>
    /// 定义单条基准回放命令的 Tick 和相对目标位置
    /// </summary>
    [Serializable]
    public struct LegacyNavigationBenchmarkCommandDefinition
    {
        [SerializeField] private int _tick;
        [SerializeField] private Vector3 _targetOffset;

        public int Tick => _tick;
        public Vector3 TargetOffset => _targetOffset;

        /// <summary>
        /// 创建一条固定 Tick 的相对移动命令
        /// </summary>
        /// <param name="tick">进入采样阶段后的相对 Tick</param>
        /// <param name="targetOffset">相对生成中心的世界空间偏移</param>
        public LegacyNavigationBenchmarkCommandDefinition(int tick, Vector3 targetOffset)
        {
            _tick = tick;
            _targetOffset = targetOffset;
        }
    }

    /// <summary>
    /// 保存可跨 32、64 和 128 Ani 场景复用的确定性命令回放
    /// </summary>
    [CreateAssetMenu(
        fileName = "SO_LegacyNavigation_Replay",
        menuName = "Animars Catcher/Benchmarks/Legacy Navigation Replay")]
    public sealed class LegacyNavigationBenchmarkReplayScript : ScriptableObject
    {
        [SerializeField] private int _randomSeed = 104729;
        [SerializeField] private LegacyNavigationBenchmarkCommandDefinition[] _commands =
            Array.Empty<LegacyNavigationBenchmarkCommandDefinition>();

        public int RandomSeed => _randomSeed;
        public IReadOnlyList<LegacyNavigationBenchmarkCommandDefinition> Commands => _commands;

        /// <summary>
        /// 使用稳定二进制序列计算随机种子和全部命令的 SHA256
        /// </summary>
        /// <returns>小写十六进制内容哈希</returns>
        public string ComputeHash()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(1);
                writer.Write(_randomSeed);
                writer.Write(_commands.Length);

                for (int i = 0; i < _commands.Length; i++)
                {
                    LegacyNavigationBenchmarkCommandDefinition command = _commands[i];
                    writer.Write(command.Tick);
                    writer.Write(command.TargetOffset.x);
                    writer.Write(command.TargetOffset.y);
                    writer.Write(command.TargetOffset.z);
                }
            }

            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(stream.ToArray());
            var builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        /// <summary>
        /// 写入由固定夹具生成器维护的随机种子和命令序列
        /// </summary>
        /// <param name="randomSeed">跨运行保持一致的随机种子</param>
        /// <param name="commands">按 Tick 递增排列的命令</param>
        public void Configure(
            int randomSeed,
            LegacyNavigationBenchmarkCommandDefinition[] commands)
        {
            _randomSeed = randomSeed;
            _commands = commands ?? Array.Empty<LegacyNavigationBenchmarkCommandDefinition>();
        }
    }
}
