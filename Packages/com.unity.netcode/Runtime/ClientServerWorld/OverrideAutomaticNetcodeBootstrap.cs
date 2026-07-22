using System;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    ///     将此组件添加到 Scene 的根 GameObject，可替换 <see cref="NetCodeConfig" /> ProjectSettings 资源中指定的自动 Bootstrap 设置
    ///     注意：NetCode 只会在 Active Scene 中搜索此 MonoBehaviour，并且只在 Bootstrap 期间搜索
    ///     Bootstrap 仅在游戏启动时发生，早于第一个 MonoBehaviour Awake
    /// </summary>
    /// <remarks>
    ///     NetCode 的 <see cref="Unity.Entities.ICustomBootstrap" />，即 <see cref="ClientServerBootstrap" />，
    ///     会使用在任意 Scene 中找到的第一个实例，存在两个或更多实例时会报错
    ///     另请注意：使用自定义 Bootstrapper 时，此组件不会自动生效
    ///     除非尽早调用 <see cref="ClientServerBootstrap.DetermineIfBootstrappingEnabled" />，并在其返回 false 时同样返回 false
    /// </remarks>
    public sealed class OverrideAutomaticNetcodeBootstrap : MonoBehaviour, IComparable<OverrideAutomaticNetcodeBootstrap>
    {
        /// <inheritdoc cref="NetCodeConfig.AutomaticBootstrapSetting" />
        [Tooltip("Note: This will only replace the bootstrap for this one scene, and only if this scene is the Active scene when entering playmode, or the first scene in the build.")]
        public NetCodeConfig.AutomaticBootstrapSetting ForceAutomaticBootstrapInScene = NetCodeConfig.AutomaticBootstrapSetting.EnableAutomaticBootstrap;

        private void OnValidate()
        {
            if(transform.root != transform)
                Debug.LogError($"OverrideAutomaticNetcodeBootstrap can only be added to the root GameObject! '{this}' is invalid, and should be moved or deleted!", this);
        }

        /// <summary>
        /// 尽量确保排序顺序具有确定性，使 Bootstrap 行为可靠
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        int IComparable<OverrideAutomaticNetcodeBootstrap>.CompareTo(OverrideAutomaticNetcodeBootstrap other)
        {
            if (ReferenceEquals(this, other)) return 0;
            if (ReferenceEquals(null, other)) return 1;
            var nameSort = string.Compare(name, other.name, StringComparison.Ordinal);
            if (nameSort != 0) return nameSort;
            return GetInstanceID().CompareTo(other.GetInstanceID());
        }
    }
}
