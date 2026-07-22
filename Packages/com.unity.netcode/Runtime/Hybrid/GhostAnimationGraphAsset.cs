using UnityEngine;
using UnityEngine.Playables;

using Unity.Entities;
using Unity.Collections;
using System;
using System.Collections.Generic;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// 此类扩展了常规 PlayableBehaviour，可用于实现 GhostAnimationController 的 Graph Asset
    /// 它新增了一个方法，用于接收 NetCode 预测循环中的更新调用
    /// 在预测中对 Graph 求值时，由于时间可能回滚，PrepareFrame 应设置其使用的所有 Clip 的时间
    /// 使用 Root Motion 时，PreparePredictedData 还应在调用开始时将所有 Clip 设为当前时间，
    /// 否则会破坏 Root Motion
    /// 仅在 isRollback 为 true 时才需要在 PreparePredictedData 中设置时间
    /// </summary>
    public abstract class GhostPlayableBehaviour : PlayableBehaviour
    {
        /// <summary>
        /// 当此 Behaviour 注册到 GhostAnimationController 后，本方法会作为预测循环的一部分被调用
        /// 除非改用 System 计算动画数据，否则所有动画数据计算都应在此方法中完成
        /// </summary>
        /// <param name="serverTick">服务器 Tick</param>
        /// <param name="deltaTime">仅当 <paramref name="isRollback"/> 为 true 时才需要设置时间</param>
        /// <param name="isRollback">是否发生回滚</param>
        public abstract void PreparePredictedData(NetworkTick serverTick, float deltaTime, bool isRollback);
    }

    /// <summary>
    /// GhostAnimationGraphAsset 用于声明其使用哪些组件存储需要进行 Ghost 同步的动画数据的接口
    /// </summary>
    public interface IRegisterPlayableData
    {
        /// <summary>
        /// 注册一个包含 Playable Data 的新组件类型
        /// 可以多次调用，同一 Controller 上的多个 Asset 也可以注册相同数据，
        /// 但用户必须确保更新数据的逻辑能够处理这种情况
        /// </summary>
        /// <typeparam name="T"><see cref="IComponentData"/> 的 Unmanaged 类型</typeparam>
        void RegisterPlayableData<T>() where T: unmanaged, IComponentData;
    }
    /// <summary>
    /// GhostAnimationController 的主 Graph Asset
    /// 所有需要同步的动画逻辑都应表示为此类型的 Asset，
    /// 该 Asset 可以引用其他 Asset 来构建完整 Graph
    /// </summary>
    public abstract class GhostAnimationGraphAsset : ScriptableObject
    {
        /// <summary>
        /// 为此节点创建 Playable
        /// Behaviours 列表必须包含所有需要调用 PreparePredictedData 的 GhostPlayableBehaviour
        /// 未加入该列表的 GhostPlayableBehaviour 不会收到预测更新调用
        /// 此方法可以创建包含 Mixer、Clip、其他 Asset 引用等内容的 GhostPlayableBehaviour
        /// </summary>
        /// <param name="controller">用于构建 Playable 的 <see cref="GhostAnimationController"/></param>
        /// <param name="graph">用于管理 Playable 创建和销毁的 <see cref="PlayableGraph"/></param>
        /// <param name="behaviours">需要调用 <see cref="GhostPlayableBehaviour.PreparePredictedData"/> 的已填充列表</param>
        /// <returns>为此节点构建的 <see cref="Playable"/></returns>
        public abstract Playable CreatePlayable(GhostAnimationController controller, PlayableGraph graph, List<GhostPlayableBehaviour> behaviours);
        /// <summary>
        /// 为此 Asset 注册 Playable Data
        /// PrepareFrame 期间只能访问在此注册的数据，无法访问其他 Entity 数据
        /// </summary>
        /// <param name="register">声明使用了哪些组件</param>
        public abstract void RegisterPlayableData(IRegisterPlayableData register);

        private class PlayableDataHashCollector : IRegisterPlayableData
        {
            public ulong Hash;
            public void RegisterPlayableData<T>() where T: unmanaged, IComponentData
            {
                // 将当前 Hash 与新组件的 Hash 合并
                var ctype = ComponentType.ReadWrite<T>();
                var typeHash = TypeManager.GetTypeInfo(ctype.TypeIndex).StableTypeHash;
                Hash = TypeHash.CombineFNV1A64(Hash, typeHash);
            }
        }
        private void OnValidate()
        {
            var hash = new PlayableDataHashCollector();
            RegisterPlayableData(hash);
            m_PlayableDataHash = hash.Hash;
        }
        [SerializeField] private ulong m_PlayableDataHash;
    }
}
