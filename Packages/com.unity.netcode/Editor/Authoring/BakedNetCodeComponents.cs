using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// GhostComponentInspector 用于保存转换后 Baking 数据的内部结构
    /// </summary>
    class BakedResult
    {
        public Dictionary<GameObject, BakedGameObjectResult> GameObjectResults;
        public GhostAuthoringComponent GhostAuthoring;

        public BakedGameObjectResult GetInspectionResult(GhostAuthoringInspectionComponent inspection)
        {
            foreach (var kvp in GameObjectResults)
            {
                if (kvp.Value.SourceInspection == inspection)
                    return kvp.Value;
            }
            return null;
        }
    }

    class BakedGameObjectResult
    {
        public BakedResult AuthoringRoot;
        public GameObject SourceGameObject;
        [CanBeNull] public GhostAuthoringInspectionComponent SourceInspection;
        public GhostAuthoringComponent RootAuthoring => AuthoringRoot.GhostAuthoring;
        public string SourcePrefabPath;
        public List<BakedEntityResult> BakedEntities;
        public int NumComponents;
    }

    /// <inheritdoc cref="BakedGameObjectResult"/>
    class BakedEntityResult
    {
        public BakedGameObjectResult GoParent;
        public Entity Entity;
        public EntityGuid Guid;
        public string EntityName;
        public int EntityIndex;
        public bool IsPrimaryEntity => EntityIndex == 0;
        public List<BakedComponentItem> BakedComponents;
        public bool IsLinkedEntity;
        public bool IsRoot => !IsLinkedEntity && GoParent.SourceGameObject == GoParent.RootAuthoring.gameObject && IsPrimaryEntity;
    }

    /// <inheritdoc cref="BakedGameObjectResult"/>
    class BakedComponentItem
    {
        public BakedEntityResult EntityParent;
        public string fullname;
        public Type managedType;
        /// <summary>
        /// 由 ComponentOverride 决定，未覆盖时使用 <see cref="defaultSerializationStrategy"/>
        /// </summary>
        public ComponentTypeSerializationStrategy serializationStrategy;
        /// <summary>
        /// 缓存默认 Variant，以便在 Inspection 界面中标记
        /// </summary>
        public ComponentTypeSerializationStrategy defaultSerializationStrategy;
        /// <summary>
        /// 该 Baked 组件可用的全部序列化策略
        /// </summary>
        public ComponentTypeSerializationStrategy[] availableSerializationStrategies;
        public string[] availableSerializationStrategyDisplayNames;

        public int entityIndex;
        public EntityGuid entityGuid => EntityParent.Guid;
        public bool anyVariantIsSerialized;
        public SendToOwnerType sendToOwnerType;

        public GhostPrefabType PrefabType => HasPrefabOverride() && GetPrefabOverride().IsPrefabTypeOverriden
            ? GetPrefabOverride().PrefabType
            : serializationStrategy.PrefabType;

        public GhostSendType SendTypeOptimization =>
            HasPrefabOverride() && GetPrefabOverride().IsSendTypeOptimizationOverriden
                ? GetPrefabOverride().SendTypeOptimization
                : serializationStrategy.SendTypeOptimization;

        public ulong VariantHash
        {
            get
            {
                if (HasPrefabOverride())
                {
                    ref var componentOverride = ref GetPrefabOverride();
                    if (componentOverride.IsVariantOverriden)
                        return componentOverride.VariantHash;
                }
                return 0;
            }
        }

        /// <summary>
        /// 表示该类型是否允许用户修改 <see cref="ComponentTypeSerializationStrategy"/>
        /// 若存在多个 Variant 类型，则隐式支持修改
        /// </summary>
        public bool DoesAllowVariantModification => serializationStrategy.HasDontSupportPrefabOverridesAttribute == 0 && serializationStrategy.IsInput == 0;

        /// <summary>
        /// 表示该类型是否允许用户修改 <see cref="SendTypeOptimization"/>
        /// </summary>
        public bool DoesAllowSendTypeOptimizationModification => serializationStrategy.HasDontSupportPrefabOverridesAttribute == 0 && anyVariantIsSerialized && !serializationStrategy.IsDontSerializeVariant && EntityParent.GoParent.RootAuthoring.SupportsSendTypeOptimization && serializationStrategy.IsInput == 0;

        /// <summary>
        /// 表示该类型是否允许用户修改 <see cref="GhostAuthoringInspectionComponent.ComponentOverride.PrefabType"/>
        /// </summary>
        public bool DoesAllowPrefabTypeModification => serializationStrategy.HasDontSupportPrefabOverridesAttribute == 0 && serializationStrategy.IsInput == 0;

        /// <summary>
        /// 表示该类型隐式支持 Prefab Override
        /// </summary>
        internal bool HasMultipleVariants => availableSerializationStrategies.Length > 1;

        internal bool HasMultipleVariantsExcludingDontSerializeVariant => HasMultipleVariants && availableSerializationStrategies.Count(x => !x.IsDontSerializeVariant) > 1;

        /// <summary>
        /// 按引用返回 Prefab Override，未找到时抛出异常，调用前可使用 <see cref="HasPrefabOverride"/> 检查
        /// </summary>
        public ref GhostAuthoringInspectionComponent.ComponentOverride GetPrefabOverride()
        {
            if (EntityParent.GoParent.SourceInspection.TryFindExistingOverrideIndex(managedType, entityGuid, out var index))
                return ref EntityParent.GoParent.SourceInspection.ComponentOverrides[index];
            throw new InvalidOperationException($"No override created for '{fullname}'! '{serializationStrategy.ToFixedString()}', EntityGuid: {entityGuid.ToString()}!");
        }

        /// <summary>
        /// 若该 Inspection Component 为此 Baked 组件类型配置了 Prefab Override，则返回 true
        /// </summary>
        public bool HasPrefabOverride()
        {
            return EntityParent.GoParent.SourceInspection != null && EntityParent.GoParent.SourceInspection.TryFindExistingOverrideIndex(managedType, entityGuid, out _);
        }

        /// <summary>
        /// 按引用返回现有 Override，不存在时创建并返回新实例
        /// </summary>
        public ref GhostAuthoringInspectionComponent.ComponentOverride GetOrAddPrefabOverride()
        {
            var defaultPrefabType = (GhostPrefabType)GhostAuthoringInspectionComponent.ComponentOverride.NoOverride;
            EntityParent.GoParent.SourceInspection.GetOrAddPrefabOverride(managedType, entityGuid, defaultPrefabType);
            return ref GetPrefabOverride();
        }

        /// <summary>
        /// 在初始化以及用户修改 Variant 时调用
        /// 确保需要时实际保存自定义 Variant
        /// </summary>
        public void SaveVariant(bool warnIfChosenIsNotAlreadySaved, bool allowSettingDefaultToRevertOverride)
        {
            if (serializationStrategy.Hash != 0 && !VariantIsTheDefault && !HasPrefabOverride())
            {
                if(warnIfChosenIsNotAlreadySaved)
                    Debug.LogError($"Discovered on ghost '{EntityParent.GoParent.SourceGameObject.name}' that in-use variant ({serializationStrategy}) was not saved as a prefabOverride! Fixed.");

                GetOrAddPrefabOverride();
            }

            if (HasPrefabOverride())
            {
                ref var @override = ref GetPrefabOverride();
                var hash = (!@override.IsVariantOverriden || allowSettingDefaultToRevertOverride) && VariantIsTheDefault ? 0 : serializationStrategy.Hash;
                if (@override.VariantHash != hash)
                {
                    @override.VariantHash = hash;
                    EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Confirmed Variant on {fullname} is {serializationStrategy}");
                }
            }
        }

        internal bool VariantIsTheDefault => serializationStrategy.Hash == defaultSerializationStrategy.Hash;

        /// <remarks>这是 Override 操作，恢复默认值属于另一种操作</remarks>
        public void TogglePrefabType(GhostPrefabType type)
        {
            var newValue = PrefabType ^ type;
            ref var @override = ref GetOrAddPrefabOverride();
            @override.PrefabType = newValue;
            EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Toggled GhostPrefabType.{type} on {fullname}, set type flag to GhostPrefabType.{newValue}");
        }

        /// <remarks>这是 Override 操作，恢复默认值属于另一种操作</remarks>
        public void SetSendTypeOptimization(GhostSendType newValue)
        {
            ref var @override = ref GetOrAddPrefabOverride();
            @override.SendTypeOptimization = newValue;
            EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Set GhostSendType.{newValue} on {fullname}, set value to GhostSendType.{newValue}");
        }

        public void RemoveEntirePrefabOverride(DropdownMenuAction action)
        {
            if (HasPrefabOverride())
            {
                serializationStrategy = defaultSerializationStrategy;
                ref var @override = ref GetPrefabOverride();
                @override.Reset();
                SaveVariant(false, true);
                EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Removed entire prefab override on {fullname}");
            }
            else GhostAuthoringInspectionComponent.forceSave = true;
        }

        public void ResetPrefabTypeToDefault(DropdownMenuAction action)
        {
            if (HasPrefabOverride())
            {
                ref var @override = ref GetPrefabOverride();
                @override.PrefabType = (GhostPrefabType) GhostAuthoringInspectionComponent.ComponentOverride.NoOverride;
                EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Reset PrefabType on {fullname}");
            }
        }

        public void ResetSendTypeToDefault(DropdownMenuAction action)
        {
            if (HasPrefabOverride())
            {
                ref var @override = ref GetPrefabOverride();
                @override.SendTypeOptimization = (GhostSendType) GhostAuthoringInspectionComponent.ComponentOverride.NoOverride;
                EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Reset SendTypeOptimization on {fullname}");
            }
        }

        public void ResetVariantToDefault()
        {
            if (HasPrefabOverride())
            {
                serializationStrategy = defaultSerializationStrategy;
                SaveVariant(false, true);
            }
        }

        public override string ToString() => $"BakedComponentItem[{fullname} with {serializationStrategy}, {availableSerializationStrategies.Length} variants available, entityGuid: {entityGuid}]";
    }
}
