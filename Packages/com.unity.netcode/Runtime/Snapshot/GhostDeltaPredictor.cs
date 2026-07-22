using Unity.Mathematics;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>
    /// 仅供内部使用，Ghost 组件 Serializer 根据前两个 Baseline 值计算并预测字段的新值
    /// </para>
    /// <para>
    /// 当变量呈线性变化或具有其他可预测规律时，此值可以较好地估计变量当前值
    /// 预测越准确，待编码增量越小，压缩率越高
    /// </para>
    /// </summary>
    public struct GhostDeltaPredictor
    {
        private int predictFrac;
        private int applyFrac;

        /// <summary>
        /// 使用最近三个 Baseline Tick 构造预测器
        /// 这些 Tick 用于计算应用到 Baseline 值上的相对权重
        /// </summary>
        /// <param name="tick">当前服务器 Tick</param>
        /// <param name="baseline0_tick">最新的 Baseline 网络 Tick</param>
        /// <param name="baseline1_tick">次新的 Baseline 网络 Tick</param>
        /// <param name="baseline2_tick">最旧的 Baseline 网络 Tick</param>
        public GhostDeltaPredictor(NetworkTick tick, NetworkTick baseline0_tick, NetworkTick baseline1_tick, NetworkTick baseline2_tick)
        {
            // 使用 4 位定点小数保存时间间隔比例，避免在 Burst 热路径中引入浮点计算
            predictFrac = 16 * baseline0_tick.TicksSince(baseline1_tick) / baseline1_tick.TicksSince(baseline2_tick);
            applyFrac = 16 * tick.TicksSince(baseline0_tick) / baseline0_tick.TicksSince(baseline1_tick);
        }

        /// <summary>
        /// 使用前三个 Baseline 计算给定整数的预测值
        /// </summary>
        /// <param name="baseline0">最新的 Tick Baseline 值</param>
        /// <param name="baseline1">次新的 Tick Baseline 值</param>
        /// <param name="baseline2">最旧的 Tick Baseline 值</param>
        /// <returns>给定整数的预测值</returns>
        public int PredictInt(int baseline0, int baseline1, int baseline2)
        {
            int delta = baseline1 - baseline2;
            // 先用两个较旧 Baseline 检验线性趋势能否准确预测最新 Baseline
            int predictBaseline = baseline1 + delta * predictFrac / 16;
            delta = baseline0 - baseline1;
            // 趋势预测不比直接保持最新值更准确时停止外推，避免放大突变带来的误差
            if (math.abs(baseline0 - predictBaseline) >= math.abs(delta))
                return baseline0;
            // 趋势可靠时，按当前 Tick 与最新 Baseline 的时间距离继续外推
            return baseline0 + delta * applyFrac / 16;
        }

        /// <summary>
        /// 使用前三个 Baseline 计算给定长整数的预测值
        /// </summary>
        /// <param name="baseline0">最新的 Tick Baseline 值</param>
        /// <param name="baseline1">次新的 Tick Baseline 值</param>
        /// <param name="baseline2">最旧的 Tick Baseline 值</param>
        /// <returns>给定长整数的预测值</returns>
        public long PredictLong(long baseline0, long baseline1, long baseline2)
        {
            long delta = baseline1 - baseline2;
            // 先用两个较旧 Baseline 检验线性趋势能否准确预测最新 Baseline
            long predictBaseline = baseline1 + delta * predictFrac / 16;
            delta = baseline0 - baseline1;
            // 趋势预测不比直接保持最新值更准确时停止外推，避免放大突变带来的误差
            if (math.abs(baseline0 - predictBaseline) >= math.abs(delta))
                return baseline0;
            // 趋势可靠时，按当前 Tick 与最新 Baseline 的时间距离继续外推
            return baseline0 + delta * applyFrac / 16;
        }
    }
}
