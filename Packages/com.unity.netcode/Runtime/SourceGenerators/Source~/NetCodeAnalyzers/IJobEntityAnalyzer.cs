using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NetCodeAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class IJobEntityAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(NetcodeDiagnostics.k_NetC0001Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeJobEntityStruct, SyntaxKind.StructDeclaration);
        }

        private void AnalyzeJobEntityStruct(SyntaxNodeAnalysisContext context)
        {
            var structDeclaration = (StructDeclarationSyntax)context.Node;

            if (!EntitiesHelpers.IsIJobEntity(structDeclaration, context.SemanticModel))
                return;

            AnalyzeNetC0001(context, structDeclaration);
        }

        private void AnalyzeNetC0001(SyntaxNodeAnalysisContext context, StructDeclarationSyntax structDeclaration)
        {
            // 其他 Analyzer 会检查当前是否位于预测 System Group 中
            // IJobEntity 结构体不一定属于某个 System，因此这里无法进行该检查并始终报告警告

            Location ignoreLocation = structDeclaration.Identifier.GetLocation();

            var hasIgnoreComponentEnabledState = HasIgnoreComponentEnabledStateInAttributes(structDeclaration, ref ignoreLocation);
            if (!hasIgnoreComponentEnabledState)
                return;

            var hasSimulate = HasSimulateInAttributes(structDeclaration, context);

            // 如果特性中未找到，则检查 Execute 方法参数
            if (!hasSimulate)
            {
                var executeMethod = GetExecuteMethod(structDeclaration);
                if (executeMethod != null)
                {
                    hasSimulate = HasSimulateInExecuteParameters(executeMethod, context);
                }
            }

            if (hasSimulate && hasIgnoreComponentEnabledState)
            {
                context.ReportDiagnostic(Diagnostic.Create(NetcodeDiagnostics.k_NetC0001Descriptor, ignoreLocation));
            }
        }

        private bool HasSimulateInAttributes(StructDeclarationSyntax structDeclaration, SyntaxNodeAnalysisContext context)
        {
            foreach (var attributeList in structDeclaration.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    foreach (var arg in attribute.ArgumentList?.Arguments ?? Enumerable.Empty<AttributeArgumentSyntax>())
                    {
                        if (arg.Expression is TypeOfExpressionSyntax typeOfExpr &&
                            ModelExtensions.GetSymbolInfo(context.SemanticModel, typeOfExpr.Type).Symbol is ITypeSymbol typeSymbol &&
                            EntitiesHelpers.IsSimulateComponent(typeSymbol))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private bool HasIgnoreComponentEnabledStateInAttributes(StructDeclarationSyntax structDeclaration, ref Location ignoreLocation)
        {
            foreach (var attributeList in structDeclaration.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    string attributeName = attribute.Name.ToString();

                    // 检查 [WithOptions] 特性
                    if (attributeName == "WithOptions" || attributeName == "WithOptionsAttribute")
                    {
                        // 检查是否有参数包含 EntityQueryOptions.IgnoreComponentEnabledState
                        foreach (var arg in attribute.ArgumentList?.Arguments ?? Enumerable.Empty<AttributeArgumentSyntax>())
                        {
                            if (arg.Expression.ToString().Contains("EntityQueryOptions.IgnoreComponentEnabledState"))
                            {
                                ignoreLocation = arg.GetLocation();
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private MethodDeclarationSyntax? GetExecuteMethod(StructDeclarationSyntax structDeclaration)
        {
            return structDeclaration.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == "Execute");
        }

        private bool HasSimulateInExecuteParameters(MethodDeclarationSyntax executeMethod, SyntaxNodeAnalysisContext context)
        {
            foreach (var parameter in executeMethod.ParameterList.Parameters)
            {
                if (parameter.Type != null &&
                    ModelExtensions.GetSymbolInfo(context.SemanticModel, parameter.Type).Symbol is ITypeSymbol typeSymbol &&
                    EntitiesHelpers.IsSimulateComponent(typeSymbol))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
