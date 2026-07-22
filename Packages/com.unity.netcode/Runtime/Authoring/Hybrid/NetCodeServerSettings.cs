#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;
using Unity.Entities.Build;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// 用于服务器构建的 <see cref="IEntitiesPlayerSettings"/> 烘焙设置
    /// 可以将 <see cref="GUID"/> 分配给 <see cref="Unity.Scenes.SceneSystemData.BuildConfigurationGUID"/>，
    /// 指示 Asset 导入工作进程使用此设置烘焙场景
    /// </summary>
    [FilePath("ProjectSettings/NetCodeServerSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class NetCodeServerSettings : ScriptableSingleton<NetCodeServerSettings>, IEntitiesPlayerSettings, INetCodeConversionTarget
    {
        NetcodeConversionTarget INetCodeConversionTarget.NetcodeTarget => NetcodeConversionTarget.Server;

        [SerializeField] private BakingSystemFilterSettings FilterSettings;
        [SerializeField] private string[] AdditionalScriptingDefines = Array.Empty<string>();

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
        ScriptableObject IEntitiesPlayerSettings.AsScriptableObject()
        {
            return instance;
        }
        internal void Save()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            if (!EditorApplication.isUpdating)
            {
                ((IEntitiesPlayerSettings) this).RegisterCustomDependency();
            }

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

    internal class ServerSettings : DotsPlayerSettingsProvider
    {
        VisualElement m_BuildSettingsContainer;

        public override string ProviderPath => "Project/Multiplayer/Build";

        public override int Importance
        {
            get { return 1; }
        }

        public override DotsGlobalSettings.PlayerType GetPlayerType()
        {
            return DotsGlobalSettings.PlayerType.Server;
        }

        protected override IEntitiesPlayerSettings DoGetSettingAsset()
        {
            return NetCodeServerSettings.instance;
        }

        public override void OnActivate(DotsGlobalSettings.PlayerType type, VisualElement rootElement)
        {
            DotsGlobalSettings.Instance.ServerProvider.ProviderPath = "Project/Multiplayer/Build";

            rootElement.RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            rootElement.RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);

            m_BuildSettingsContainer = new VisualElement();
            m_BuildSettingsContainer.AddToClassList("target");

            var so = new SerializedObject(NetCodeServerSettings.instance);
            m_BuildSettingsContainer.Bind(so);
            so.Update();

            var label = new Label("Server");
            m_BuildSettingsContainer.Add(label);

            var targetS = new VisualElement();
            targetS.AddToClassList("target-Settings");
            var propServerSettings = so.FindProperty("FilterSettings.ExcludedBakingSystemAssemblies");
            var propServerField = new PropertyField(propServerSettings);
            propServerField.BindProperty(propServerSettings);
            propServerField.RegisterCallback<ChangeEvent<string>>(
                evt =>
                {
                    NetCodeServerSettings.instance.GetFilterSettings().SetDirty();
                });
            targetS.Add(propServerField);

            var propExtraDefines = so.FindProperty("AdditionalScriptingDefines");
            var propExtraDefinesField = new PropertyField(propExtraDefines);
            propExtraDefinesField.name = "Extra Defines";
            targetS.Add(propExtraDefinesField);

            m_BuildSettingsContainer.Add(targetS);
            rootElement.Add(m_BuildSettingsContainer);

            so.ApplyModifiedProperties();
        }

        static void OnAttachToPanel(AttachToPanelEvent evt)
        {
            // ScriptableSingleton<T> 默认不能直接编辑
            // 修改 hideFlags 使 SerializedObject 可编辑
            NetCodeServerSettings.instance.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        }

        static void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            // 恢复原始标志
            NetCodeServerSettings.instance.hideFlags = HideFlags.HideAndDontSave;
            NetCodeServerSettings.instance.Save();
        }

        public override string[] GetExtraScriptingDefines()
        {
            var extraDefines = GetSettingAsset().GetAdditionalScriptingDefines().Append("UNITY_SERVER");
#if !NETCODE_NDEBUG
            if (EditorUserBuildSettings.development)
                extraDefines = extraDefines.Append("NETCODE_DEBUG");
#endif
            return extraDefines.ToArray();
        }

        public override BuildOptions GetExtraBuildOptions()
        { // DOTS-5792
#pragma warning disable 618
            return BuildOptions.EnableHeadlessMode;
#pragma warning restore 618
        }
    }
}
#endif
