using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NetCodeAnalyzer
{
    public static class EntitiesHelpers
    {
        public static bool IsSimulateComponent(ITypeSymbol typeSymbol)
        {
            return typeSymbol.Name == "Simulate" &&
                   typeSymbol.ContainingNamespace?.ToString() == "Unity.Entities";
        }

        public static bool IsIJobEntity(StructDeclarationSyntax structDeclaration, SemanticModel semanticModel)
        {
            var iJobEntityType = semanticModel.Compilation.GetTypeByMetadataName("Unity.Entities.IJobEntity");
            if (iJobEntityType == null)
                return false;

            var typeSymbol = semanticModel.GetDeclaredSymbol(structDeclaration);
            return typeSymbol != null && typeSymbol.AllInterfaces.Contains(iJobEntityType);
        }

        public static bool IsWithSimulate(InvocationExpressionSyntax invocation, SyntaxNodeAnalysisContext context, bool checkNestedStruct)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                // 检查泛型参数
                if (memberAccess.Name is GenericNameSyntax genericName)
                {
                    foreach (var typeArg in genericName.TypeArgumentList.Arguments)
                    {
                        if (checkNestedStruct) // 检查 Simulate 是否用于方法链的嵌套泛型参数，例如 Query<RefRO<Simulate>>()
                        {
                            if (context.SemanticModel.GetSymbolInfo(typeArg).Symbol is INamedTypeSymbol nestedTypeSymbol &&
                                nestedTypeSymbol.IsGenericType)
                            {
                                foreach (var innerTypeArgs in nestedTypeSymbol.TypeArguments)
                                {
                                    if (IsSimulateComponent(innerTypeArgs))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }

                        // 检查 Simulate 是否直接用于方法链，例如 EntityQueryBuilder().WithAll<Simulate>()
                        if (context.SemanticModel.GetSymbolInfo(typeArg).Symbol is ITypeSymbol typeSymbol &&
                            IsSimulateComponent(typeSymbol))
                        {
                            return true;
                        }
                    }
                }

                // 检查普通参数
                foreach (var arg in invocation.ArgumentList.Arguments)
                {
                    if (arg.Expression is TypeOfExpressionSyntax typeOfExpr &&
                        context.SemanticModel.GetSymbolInfo(typeOfExpr.Type).Symbol is ITypeSymbol typeSymbol
                        && IsSimulateComponent(typeSymbol))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool IsWithOptionsIgnoreEnabled(InvocationExpressionSyntax invocation, ref Location ignoreLocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "WithOptions")
            {
                foreach (var arg in invocation.ArgumentList.Arguments)
                {
                    if (arg.Expression.ToString().Contains("EntityQueryOptions.IgnoreComponentEnabledState"))
                    {
                        ignoreLocation = arg.Expression.GetLocation();
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool IsInvocationInPredictionSystemGroup(SyntaxNode invocation, SyntaxNodeAnalysisContext context)
        {
            var containingType = invocation.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (containingType == null)
                return false;

            var semanticModel = context.SemanticModel;
            var typeSymbol = semanticModel.GetDeclaredSymbol(containingType);
            if (typeSymbol == null) // 不要求目标实际为 System，只需要它具有 UpdateInGroup 特性
                return false;

            var targetGroupType = semanticModel.Compilation.GetTypeByMetadataName("Unity.NetCode.PredictedSimulationSystemGroup");
            return !SymbolEqualityComparer.Default.Equals(targetGroupType, null) && IsInGroupHierarchy(typeSymbol, targetGroupType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
        }

        public static bool IsInGroupHierarchy(ITypeSymbol systemType, ITypeSymbol targetGroupType, HashSet<ITypeSymbol> visitedGroups)
        {
            var updateInGroupAttr = systemType.GetAttributes()
                .FirstOrDefault(attr => attr.AttributeClass?.Name == "UpdateInGroupAttribute");

            if (updateInGroupAttr == null)
                return false;

            if (updateInGroupAttr.ConstructorArguments.Length == 0)
                return false;

            var directGroupType = updateInGroupAttr.ConstructorArguments[0].Value as ITypeSymbol;
            if (directGroupType == null)
                return false;

            if (SymbolEqualityComparer.Default.Equals(directGroupType, targetGroupType) ||
                InheritsFromType(directGroupType, targetGroupType))
                return true;

            if (!visitedGroups.Add(directGroupType))
                return false;

            // 递归检查直接所属组是否位于目标组中
            return IsInGroupHierarchy(directGroupType, targetGroupType, visitedGroups);
        }

        public static bool InheritsFromType(ITypeSymbol typeSymbol, ITypeSymbol baseType)
        {
            var currentType = typeSymbol.BaseType;

            while (currentType != null)
            {
                if (SymbolEqualityComparer.Default.Equals(currentType, baseType))
                    return true;

                currentType = currentType.BaseType;
            }

            return false;
        }
    }
}
