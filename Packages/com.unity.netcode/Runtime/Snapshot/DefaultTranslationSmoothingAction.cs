using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.NetCode
{
    /*
        示例 1：
        为 Predicted Ghost 上的 Translation 注册 DefaultTranslationSmoothingAction

        World.GetSingleton<GhostPredictionSmoothing>().RegisterSmoothingAction<Translation>(EntityManager, DefaultTranslationSmoothingAction.Action);

        示例 2：
        此处还将 DefaultUserParamsComponent 注册为用户数据
        注意 DefaultSmoothingActionUserParams 必须附加到 PredictedGhost

        World.GetSingleton<GhostPredictionSmoothing>().RegisterSmoothingAction<Translation, DefaultUserParams>(EntityManager, DefaultTranslationSmoothingAction.Action);
    */

    /// <summary>
    /// 添加 DefaultSmoothingActionUserParams Component，按 Entity 自定义启用位置平滑的预测误差范围
    /// </summary>
    [GhostComponent(PrefabType = GhostPrefabType.PredictedClient)]
    public struct DefaultSmoothingActionUserParams : IComponentData
    {
        /// <summary>
        /// 预测误差大于此值时，将 Entity 位置直接 Snap 到新值
        /// </summary>
        public float maxDist;
        /// <summary>
        /// 预测误差小于此值时，将 Entity 位置直接 Snap 到新值
        /// </summary>
        public float delta;
    }

    /// <summary>
    /// <see cref="Translation"/> Component 的默认预测误差 <see cref="SmoothingAction"/> 函数
    /// 支持通过用户数据自定义 Translation Component 的限制和 Snap 行为，用于 Translation 预测误差过大的情况
    /// </summary>
    [BurstCompile]
    public unsafe struct DefaultTranslationSmoothingAction
    {
        /// <summary>
        /// 函数未收到用户数据时使用的 <see cref="DefaultSmoothingActionUserParams"/> 默认值
        /// 预测误差至少为 1 个单位且小于 10 个单位时修正位置，单位通常为米
        /// </summary>
        public sealed class DefaultStaticUserParams
        {
            /// <summary>
            /// 预测误差大于此值时，将 Entity 位置直接 Snap 到新值
            /// 默认阈值为 10 个单位
            /// </summary>
            public static readonly SharedStatic<float> maxDist = SharedStatic<float>.GetOrCreate<DefaultStaticUserParams, MaxDistKey>();
            /// <summary>
            /// 预测误差小于此值时，将 Entity 位置直接 Snap 到新值
            /// 默认阈值为 1 个单位
            /// </summary>
            public static readonly SharedStatic<float> delta = SharedStatic<float>.GetOrCreate<DefaultStaticUserParams, DeltaKey>();

            static DefaultStaticUserParams()
            {
                maxDist.Data = 10;
                delta.Data = 1;
            }
            class MaxDistKey {}
            class DeltaKey {}
        }

        /// <summary>
        /// 返回 Burst 兼容函数指针，可用于向 <see cref="GhostPredictionSmoothing"/> Singleton 注册平滑 Action
        /// </summary>
        public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate> Action = new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostPredictionSmoothing.SmoothingActionDelegate))]
        private static void SmoothingAction(IntPtr currentData, IntPtr previousData, IntPtr usrData)
        {
            ref var trans = ref UnsafeUtility.AsRef<LocalTransform>((void*)currentData);
            ref var backup = ref UnsafeUtility.AsRef<LocalTransform>((void*)previousData);

            float maxDist = DefaultStaticUserParams.maxDist.Data;
            float delta = DefaultStaticUserParams.delta.Data;

            if (usrData.ToPointer() != null)
            {
                ref var userParam = ref UnsafeUtility.AsRef<DefaultSmoothingActionUserParams>(usrData.ToPointer());
                maxDist = userParam.maxDist;
                delta = userParam.delta;
            }

            var dist = math.distance(trans.Position, backup.Position);
            if (dist < maxDist && dist > delta && dist > 0)
            {
                trans.Position = backup.Position + (trans.Position - backup.Position) * delta / dist;
            }
        }
    }
}
