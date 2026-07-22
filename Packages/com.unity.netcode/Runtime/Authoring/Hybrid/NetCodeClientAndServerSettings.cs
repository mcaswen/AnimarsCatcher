#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities.Build;
using UnityEditor;
using UnityEngine;
using UnityEngine.PlayerLoop;
using UnityEngine.UIElements;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// 用于服务器构建的 <see cref="IEntitiesPlayerSettings"/> 烘焙设置
    /// 可以将 <see cref="GUID"/> 分配给 <see cref="Unity.Scenes.SceneSystemData.BuildConfigurationGUID"/>，
    /// 指示 Asset 导入工作进程使用此设置烘焙场景
    /// </summary>
    [FilePath("ProjectSettings/NetCodeClientAndServerSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class NetCodeClientAndServerSettings : ScriptableSingleton<NetCodeClientAndServerSettings>, IEntitiesPlayerSettings, INetCodeConversionTarget
    {
        NetcodeConversionTarget INetCodeConversionTarget.NetcodeTarget => NetcodeConversionTarget.ClientAndServer;

        [SerializeField] private BakingSystemFilterSettings FilterSettings;

        [SerializeField] private string[] AdditionalScriptingDefines = Array.Empty<string>();

        /// <summary>
        ///     自动添加到构建中的 <see cref="NetCodeConfig"/>，用户代码可通过 <see cref="NetCodeConfig.Global"/> 访问
        /// </summary>
        [SerializeField] public NetCodeConfig GlobalNetCodeConfig;

        /// <inheritdoc cref="EditorImportanceSuggestion"/>
        [SerializeField] public List<EditorImportanceSuggestion> CurrentImportanceSuggestions = new List<EditorImportanceSuggestion>
        {
            new () { MinValue = 1, MaxValue = 4, Name = "Low Importance", Tooltip = "For cosmetic (i.e. visual-only) ghosts like glass bottles, signs, beach-balls, and cones etc. Typically <b>Static</b>.", },
            new () { MinValue = 5, MaxValue = 40, Name = "Medium Importance", Tooltip = "For common gameplay-affecting ghosts like trees, doors, explosive barrels, dropped loot etc. Typically <b>Static</b>.", },
            new () { MinValue = 50, MaxValue = 250, Name = "High Importance", Tooltip = "For per-player and objective-critical ghosts like Player Character Controllers and CTF flags etc. Typically for <b>Dynamic</b> i.e. <b>Predicted</b> ghosts. <b>UsePreSerialization</b> is likely a good fit.", },
            new () { MinValue = 1000, MaxValue = 0, Name = "Critical Importance", Tooltip = "For gameplay critical singletons like the one keeping the current score, or the one denoting whether or not the current round has started etc. Choose <b>UsePreSerialization</b>, and use sparingly.", },
        };

        static Entities.Hash128 s_Guid;
        /// <inheritdoc/>
        public Entities.Hash128 GUID
        {
            get
            {
                if (!s_Guid.IsValid)
                    s_Guid = UnityEngine.Hash128.Compute(GetFilePath());
                return s_Guid;
            }
        }

        /// <inheritdoc/>
        public string CustomDependency => GetFilePath();

        /// <inheritdoc/>
        void IEntitiesPlayerSettings.RegisterCustomDependency()
        {
            if (!AssetDatabase.IsAssetImportWorkerProcess())
            {
                var hash = GetHash();
                AssetDatabase.RegisterCustomDependency(CustomDependency, hash);
            }
        }
        /// <inheritdoc/>
        public UnityEngine.Hash128 GetHash()
        {
            var hash = (UnityEngine.Hash128)GUID;
            if (FilterSettings?.ExcludedBakingSystemAssemblies != null)
                foreach (var assembly in FilterSettings.ExcludedBakingSystemAssemblies)
                {
                    var guid = AssetDatabase.GUIDFromAssetPath(AssetDatabase.GetAssetPath(assembly.asset));
                    hash.Append(ref guid);
                }
            foreach (var define in AdditionalScriptingDefines)
                hash.Append(define);
            return hash;
        }
        /// <inheritdoc/>
        public BakingSystemFilterSettings GetFilterSettings()
        {
            return FilterSettings;
        }
        /// <inheritdoc/>
        public string[] GetAdditionalScriptingDefines()
        {
            return AdditionalScriptingDefines;
        }
        /// <inheritdoc/>
        ScriptableObject IEntitiesPlayerSettings.AsScriptableObject() => instance;

        internal void Save()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
            Save(true);
            AssetDatabase.Refresh();
        }

#if UNITY_2023_2_OR_NEWER
        private void OnEnable()
        {
            if (!AssetDatabase.IsAssetImportWorkerProcess())
            {
                ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
            }
        }
#endif
        private void OnDisable()
        {
#if !UNITY_2023_2_OR_NEWER
            Save();
#else
            // 重新启用 ScriptableObject 时会更新依赖
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            Save(true);
            // 此保护必不可少，因为在编辑器刷新数据库时调用 RegisterCustomDependency 会抛出异常
            if(!EditorApplication.isUpdating)
            {
                ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
                AssetDatabase.Refresh();
            }
#endif
        }
    }

    /// <summary>
    /// 仅供编辑器使用的辅助类型，用于配置 <see cref="GhostAuthoringComponent.Importance"/> Tooltip 中针对具体数值的建议范围
    /// </summary>
    [Serializable]
    public struct EditorImportanceSuggestion
    {
        /// <summary>
        /// 宽松的最小值
        /// </summary>
        public float MinValue;
        /// <summary>
        /// 宽松的最大值
        /// </summary>
        public float MaxValue;
        /// <summary>
        /// 此重要度类别或范围的简短内联名称
        /// </summary>
        public string Name;
        /// <summary>
        /// 适用场景的单行示例
        /// </summary>
        public string Tooltip;
        /// <summary>
        /// 辅助方法
        /// </summary>
        /// <returns>格式化后的字符串</returns>
        public override string ToString() => $"{MinValue} ~ {MaxValue} for {Name} - {Tooltip}";
    }
}
#endif
