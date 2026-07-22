using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Unity.NetCode.Generators;

public static class FixedListUtils
{
    // FixedList 元素必须是 unmanaged 类型且使用顺序布局，前一个条件已由外部保证
    // 此处不允许自动布局
    // bool 有时会导致自动布局异常，但从 sizeof() 计算角度看结构体对齐应保持一致
    // 可以将 bool 视为按字节对齐
    public static Diagnostic VerifyFixedListStructRequirement(ITypeSymbol fixedListType)
    {
        var structLayoutAttribute = Roslyn.Extensions.GetAttribute(fixedListType, "System.Runtime.InteropServices", "StructLayoutAttribute");
        if (structLayoutAttribute == null)
            return null;
        if (structLayoutAttribute.ConstructorArguments.Length == 0 ||
            structLayoutAttribute.ConstructorArguments[0].Type.Name != "LayoutKind")
            return null;
        // 只支持顺序布局
        var layoutKind = (structLayoutAttribute.ConstructorArguments[0]).ToCSharpString();
        if (layoutKind != "System.Runtime.InteropServices.LayoutKind.Sequential")
        {
            var diagnosticDescriptor = Diagnostic.Create(DiagnosticHelper.CreateErrorDescriptor($"Unsupported {layoutKind} layout type specified for {fixedListType.ToDisplayString()}. The only supported layout type for FixedList[32,64,128,512,4096]<T> type argument is the LayoutKind.Sequential."),
                fixedListType.Locations[0]);
            return diagnosticDescriptor;
        }
        return null;
    }

    public static (int, int) CalculateStructSizeOf(ITypeSymbol typeSymbol)
    {
        return CalculateStructSizeOf_Recursive(typeSymbol);
    }
    public static int CalculateNumElements(ITypeSymbol fixedListSymbol)
    {
        var sizeAndAlignment = CalculateStructSizeOf_Recursive(((INamedTypeSymbol)fixedListSymbol).TypeArguments[0]);
        var byteSize = fixedListSymbol.Name.Substring(9, fixedListSymbol.Name.IndexOf('B')-9);
        // 减去 2 是因为前两个字节保留给列表长度
        // 之后还需减去元素对齐填充；虽然可以省略，但为保证计算方式完全兼容仍在此计入
        var storageSize = int.Parse(byteSize) - 2 - PaddingBytes(sizeAndAlignment.Item2);
        int numElements = storageSize / sizeAndAlignment.Item1;
        return numElements;
    }

    private static int PaddingBytes(int alignment)
    {
        return System.Math.Min(0, System.Math.Min(6, alignment - 2));
    }

    private static (int, int) CalculateStructSizeOf_Recursive(ITypeSymbol typeSymbol)
    {
        if (Roslyn.Extensions.IsEnum(typeSymbol))
        {
            int alignment = Roslyn.Extensions.PrimitiveTypeAlignment(((INamedTypeSymbol)typeSymbol).EnumUnderlyingType);
            return (alignment, alignment);
        }
        if (typeSymbol.SpecialType != SpecialType.None)
        {
            int alignment = Roslyn.Extensions.PrimitiveTypeAlignment(typeSymbol);
            return (alignment, alignment);
        }
        int structSize = 0;
        int structAlignment = 1;
        var members = typeSymbol.GetMembers();
        foreach (var f in members)
        {
            if(f.IsStatic)
                continue;
            if (f.Kind != SymbolKind.Field && f.Kind != SymbolKind.Property)
                continue;
            if(f.Kind == SymbolKind.Property && ((f as IPropertySymbol).IsIndexer || !f.IsImplicitlyDeclared))
                continue;

            int fieldSize = 0, fieldAlignment = 1;
            if (f.Kind == SymbolKind.Field)
            {
                (fieldSize, fieldAlignment) = CalculateStructSizeOf_Recursive(((IFieldSymbol)f).Type);
            }
            else if(f.Kind == SymbolKind.Property && f.IsImplicitlyDeclared)
            {
                (fieldSize, fieldAlignment) = CalculateStructSizeOf_Recursive(((IPropertySymbol)f).Type);
            }
            // 其他成员不会增加结构体大小
            if ((structSize % fieldAlignment) != 0)
            {
                structSize = (structSize + fieldAlignment - 1) & ~(fieldAlignment - 1);
            }
            structSize += fieldSize;
            if (fieldAlignment > structAlignment)
                structAlignment = fieldAlignment;
        }
        structSize = structSize + (structAlignment - 1) & ~(structAlignment - 1);
        return (structSize, structAlignment);
    }
}
