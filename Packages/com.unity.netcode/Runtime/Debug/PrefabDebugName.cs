#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting.APIUpdating;

#if NETCODE_DEBUG
namespace Unity.NetCode
{
    /// <summary>
    /// Ghost Prefab 的名称，用于在调试时以易读形式输出 Ghost 名称
    /// 仅在定义 NETCODE_DEBUG 后可用
    /// </summary>
    public struct PrefabDebugName : IComponentData
    {
        /// <summary>
        /// Prefab 名称
        /// </summary>
        [Obsolete("The PrefabDebugName.Name field has been deprecated. Please use the PrefabName instead.", true)]
        public FixedString64Bytes Name
        {
            readonly get
            {
                var fs = default(FixedString64Bytes);
                fs.CopyFromTruncated(PrefabName);
                return fs;
            }
            // ReSharper disable once ValueParameterNotUsed
            set {}
        }

        /// <summary>
        /// Prefab 名称
        /// </summary>
        public LowLevel.BlobStringText PrefabName;
    }
}
#endif
