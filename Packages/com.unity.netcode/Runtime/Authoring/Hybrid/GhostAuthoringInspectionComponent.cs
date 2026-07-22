using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>可选择添加到 Ghost Prefab 中任意或全部 GameObject 的 MonoBehaviour，用于检查并保存 Ghost 元数据，例如：</para>
    /// <para> - 覆盖或调整子实体与根实体上的部分组件复制属性</para>
    /// <para> - 为每个组件指定要使用的 <see cref="GhostComponentVariationAttribute">变体</see></para>
    /// </summary>
    /// <seealso cref="GhostAuthoringComponent"/>
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostAuthoringInspetionComponent)]
    public class GhostAuthoringInspectionComponent : MonoBehaviour
    {
        // TODO：当前不支持多对象编辑
        internal static bool forceBake;
        internal static bool forceRebuildInspector = true;
        internal static bool forceSave;

        /// <summary>
        /// 用户应用到此 Entity 的全部已保存修改列表
        /// 如果未设置，则默认使用用户在每个 <see cref="GhostInstance"/> 上配置的特性值
        /// </summary>
        [FormerlySerializedAs("m_ComponentOverrides")]
        [SerializeField]
        internal ComponentOverride[] ComponentOverrides = Array.Empty<ComponentOverride>();

        ///<summary>这不是最快的方式，但查找类型平均只需要约 10～50 微秒或更少，
        ///因此即使每个 Prefab 包含数十个组件，速度也尚可接受</summary>
        static Type FindTypeFromFullTypeNameInAllAssemblies(string fullName)
        {
            // TODO：考虑使用 TypeManager
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = a.GetType(fullName, false);
                if (type != null)
                    return type;
            }
            return default;
        }

        [ContextMenu("Force Re-Bake Prefab")]
        void ForceBake()
        {
            forceBake = true;
            forceRebuildInspector = true;
        }

        /// <summary>

        /// 报告所有无效的覆盖设置

        /// </summary>
        internal void LogErrorIfComponentOverrideIsInvalid()
        {
            for (var i = 0; i < ComponentOverrides.Length; i++)
            {
                ref var mod = ref ComponentOverrides[i];
                var compType = FindTypeFromFullTypeNameInAllAssemblies(mod.FullTypeName);
                if (compType == null)
                {
                    Debug.LogError($"Ghost Prefab '{name}' has an invalid 'Component Override' targeting an unknown component type '{mod.FullTypeName}'. " +
                                   "If this type has been renamed, you will unfortunately need to manually re-add this override. If it has been deleted, simply re-commit this prefab.");
                }
            }
        }

        /// <remarks>请注意，此操作不会自动保存，请确保调用 <see cref="SavePrefabOverride"/></remarks>
        internal ref ComponentOverride GetOrAddPrefabOverride(Type managedType, EntityGuid entityGuid, GhostPrefabType defaultPrefabType)
        {
            if (!gameObject || !this)
                throw new ArgumentException($"Attempting to GetOrAddPrefabOverride for entityGuid '{entityGuid}' to '{this}', but GameObject and/or InspectionComponent has been destroyed!");

            if (gameObject.GetInstanceID() != entityGuid.OriginatingId && !TryGetFirstMatchingGameObjectInChildren(gameObject.transform, entityGuid, out _))
            {
                throw new ArgumentException($"Attempting to GetOrAddPrefabOverride for entityGuid '{entityGuid}' to '{this}', but entityGuid does not match our gameObject, nor our children!");
            }

            if (TryFindExistingOverrideIndex(managedType, entityGuid, out var index))
            {
                return ref ComponentOverrides[index];
            }

            // 未找到，因此新增一项
            ref var found = ref AddComponentOverrideRaw();
            found = new ComponentOverride
            {
                EntityIndex = entityGuid.b,
                FullTypeName = managedType.FullName,
            };
            found.Reset();
            found.PrefabType = defaultPrefabType;
            return ref found;
        }

        internal ref ComponentOverride AddComponentOverrideRaw()
        {
            Array.Resize(ref ComponentOverrides, ComponentOverrides.Length + 1);
            return ref ComponentOverrides[ComponentOverrides.Length - 1];
        }

        /// <summary>

        /// 保存此组件覆盖设置，如果它仍为默认值则尝试移除

        /// </summary>
        internal void SavePrefabOverride(ref ComponentOverride componentOverride, string reason)
        {
            forceSave = true;

            // 如果该设置已不再覆盖任何内容，则将其完全移除
            if (!componentOverride.HasOverriden)
            {
                var index = FindExistingOverrideIndex(ref componentOverride);
                RemoveComponentOverrideByIndex(index);
            }
        }

        /// <summary>

        /// 使用最后一个元素替换此元素，然后将数组长度减一

        /// </summary>
        /// <param name="index">要移除的索引</param>
        internal void RemoveComponentOverrideByIndex(int index)
        {
            if (ComponentOverrides.Length == 0) return;
            if (index < ComponentOverrides.Length - 1)
            {
                ComponentOverrides[index] = ComponentOverrides[ComponentOverrides.Length - 1];
            }
            Array.Resize(ref ComponentOverrides, ComponentOverrides.Length - 1);
        }

        int FindExistingOverrideIndex(ref ComponentOverride currentOverride)
        {
            for (int i = 0; i < ComponentOverrides.Length; i++)
            {
                if (string.Equals(ComponentOverrides[i].FullTypeName, currentOverride.FullTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            throw new InvalidOperationException("Unable to find index of override, which should be impossible as we're passing currentOverride by ref!");
        }

        /// <summary>

        /// 执行深度优先搜索，在 Transform 层级中查找与此 EntityGuid 匹配的元素

        /// </summary>
        /// <param name="current">开始搜索的根元素</param>
        /// <param name="entityGuid">查询目标：第一个与此 EntityGuid 匹配的元素</param>
        /// <param name="foundGameObject">第一个匹配查询的元素，否则设为 null</param>
        /// <returns>找到时返回 true</returns>
        static bool TryGetFirstMatchingGameObjectInChildren(Transform current, EntityGuid entityGuid, out GameObject foundGameObject)
        {
            if (current.gameObject.GetInstanceID() == entityGuid.OriginatingId)
            {
                foundGameObject = current.gameObject;
                return true;
            }

            if (current.childCount == 0)
            {
                foundGameObject = null;
                return false;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);
                if (TryGetFirstMatchingGameObjectInChildren(child, entityGuid, out foundGameObject))
                {
                    return true;
                }
            }
            foundGameObject = null;
            return false;
        }

        /// <summary>

        /// 查找此 Ghost Authoring Prefab 及其子对象上的所有 <see cref="GhostAuthoringInspectionComponent"/>，并把全部 <see cref="ComponentOverrides"/> 添加到一个列表

        /// </summary>
        /// <param name="ghostAuthoring">开始搜索的根 Prefab</param>
        /// <param name="validate"></param>
        internal static List<(GameObject, ComponentOverride)> CollectAllComponentOverridesInInspectionComponents(GhostAuthoringComponent ghostAuthoring, bool validate)
        {
            var inspectionComponents = CollectAllInspectionComponents(ghostAuthoring);
            var allComponentOverrides = new List<(GameObject, ComponentOverride)>(inspectionComponents.Count * 4);
            foreach (var inspectionComponent in inspectionComponents)
            {
                if(validate)
                    inspectionComponent.LogErrorIfComponentOverrideIsInvalid();

                foreach (var componentOverride in inspectionComponent.ComponentOverrides)
                {
                    allComponentOverrides.Add((inspectionComponent.gameObject, componentOverride));
                }
            }

            return allComponentOverrides;
        }

        internal static List<GhostAuthoringInspectionComponent> CollectAllInspectionComponents(GhostAuthoringComponent ghostAuthoring)
        {
            var inspectionComponents = new List<GhostAuthoringInspectionComponent>(8);
            ghostAuthoring.gameObject.GetComponents(inspectionComponents);
            ghostAuthoring.GetComponentsInChildren(inspectionComponents);
            return inspectionComponents;
        }

        /// <summary>

        /// 已保存的覆盖值

        /// </summary>
        [Serializable]
        internal struct ComponentOverride : IComparer<ComponentOverride>, IComparable<ComponentOverride>
        {
            public const int NoOverride = -1;

            ///<summary>
            /// 为便于序列化，这里使用类型全名，因为不能依赖组件的 TypeIndex
            /// 也不能使用 StableTypeHash，因为布局或字段变化同样会影响该哈希值，因此它不适合此用途
            /// </summary>
            public string FullTypeName;

            /// <summary>

            /// Entity GUID 索引引用

            /// </summary>
            [FormerlySerializedAs("EntityGuid")] public ulong EntityIndex;

            /// <summary>

            /// 覆盖此类型可用的模式，如果为 `None`，则从 Prefab 或 Entity 实例中移除此组件

            /// </summary>
            /// <remarks>请注意，<see cref="VariantHash"/> 可能覆盖此值</remarks>
            public GhostPrefabType PrefabType;

            /// <summary>

            /// 如果能够确定，则覆盖组件要发送到的客户端类型

            /// </summary>
            [FormerlySerializedAs("OwnerPredictedSendType")]
            public GhostSendType SendTypeOptimization;

            /// <summary>

            /// 选择要使用的变体，0 表示默认变体

            /// </summary>
            public ulong VariantHash;

            /// <summary>

            /// 表示此 ComponentOverride 已知且已正确配置的标志

            /// </summary>
            [NonSerialized]public bool DidCorrectlyMap;

            public bool HasOverriden => IsPrefabTypeOverriden || IsSendTypeOptimizationOverriden || IsVariantOverriden;

            public bool IsPrefabTypeOverriden => (int)PrefabType != NoOverride;

            public bool IsSendTypeOptimizationOverriden => (int)SendTypeOptimization != NoOverride;

            public bool IsVariantOverriden => VariantHash != 0;

            public void Reset()
            {
                PrefabType = (GhostPrefabType)NoOverride;
                SendTypeOptimization = (GhostSendType)NoOverride;
                VariantHash = 0;
            }

            public override string ToString()
            {
                return $"ComponentOverride['{FullTypeName}', EntityIndex:'{EntityIndex}', prefabType:{PrefabType}, sto:{SendTypeOptimization}, variantH:{VariantHash}]";
            }

            public int Compare(ComponentOverride x, ComponentOverride y)
            {
                var fullTypeNameComparison = string.Compare(x.FullTypeName, y.FullTypeName, StringComparison.Ordinal);
                if (fullTypeNameComparison != 0) return fullTypeNameComparison;
                var entityGuidComparison = x.EntityIndex.CompareTo(y.EntityIndex);
                return entityGuidComparison != 0 ? entityGuidComparison : x.VariantHash.CompareTo(y.VariantHash);
            }

            public int CompareTo(ComponentOverride other)
            {
                return Compare(this, other);
            }
        }

        internal bool TryFindExistingOverrideIndex(Type managedType, in EntityGuid guid, out int index)
        {
            var managedTypeFullName = managedType.FullName;
            return TryFindExistingOverrideIndex(managedTypeFullName, guid.b, out index);
        }

        internal bool TryFindExistingOverrideIndex(string managedTypeFullName, in ulong entityGuid, out int index)
        {
            for (index = 0; index < ComponentOverrides.Length; index++)
            {
                ref var componentOverride = ref ComponentOverrides[index];
                if (componentOverride.EntityIndex == entityGuid && string.Equals(componentOverride.FullTypeName, managedTypeFullName, StringComparison.OrdinalIgnoreCase))
                {
                    componentOverride.DidCorrectlyMap = true;
                    return true;
                }
            }
            index = -1;
            return false;
        }
    }
}
