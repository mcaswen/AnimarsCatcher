using System;

namespace Unity.NetCode.Generators
{
    // 以下 Enum 是 NetCode.GhostModifiers 中对应定义的副本
    // Package 中这些 Enum 的任何变更也必须同步到这里
    // 虽然并非必须使用 Enum，但它便于逐值对应并生成正确名称

    enum SmoothingAction
    {
        Clamp = 0,
        Interpolate = 1,
        InterpolateAndExtrapolate = 3,
    }

    [Flags]
    enum GhostPrefabType
    {
        None = 0,
        InterpolatedClient = 1,
        PredictedClient = 2,
        Client = 3,
        Server = 4,
        AllPredicted = 6,
        All = 7
    }

    [Flags]
    enum GhostSendType
    {
        DontSend = 0,
        OnlyInterpolatedClients = 1,
        OnlyPredictedClients = 2,
        AllClients = 3
    }

    [Flags]
    enum SendToOwnerType
    {
        None = 0,
        SendToOwner = 1,
        SendToNonOwner = 2,
        All = 3,
    }

    // GhostFieldAttribute 的内部表示，用于配置 TypeInformation 的 Attribute 字段
    class GhostField
    {
        public int Quantization { get; set; } = -1;
        public SmoothingAction Smoothing { get; set; }
        public int SubType { get; set; }
        public float MaxSmoothingDistance { get; set; }
        public bool ?Composite { get; set; }
        public bool SendData { get; set; } = true;
    }

    // NetCode Package 中 TypeRegistryEntry 的内部副本
    // 用于声明默认类型注册表，也供用户在 UserDefinedTemplate.RegisterTemplates 中指定自定义类型列表
    // NetCode/Authoring/TypeRegistryEntry.cs 的任何变更都必须同步到这里
    class TypeRegistryEntry
    {
        public string Type;
        public string Template;
        public string TemplateOverride;
        public int SubType;
        public SmoothingAction Smoothing;
        public bool Quantized;
        public bool SupportCommand;
        public bool Composite;

        public override string ToString()
        {
            return $"{nameof(TypeRegistryEntry)}:[{nameof(Type)}: {Type}, {nameof(Template)}: {Template}, {nameof(TemplateOverride)}: {TemplateOverride}, {nameof(SubType)}: {SubType}, {nameof(Smoothing)}: {Smoothing}, {nameof(Quantized)}: {Quantized}, {nameof(SupportCommand)}: {SupportCommand}, {nameof(Composite)}: {Composite}]";
        }
    }
}
