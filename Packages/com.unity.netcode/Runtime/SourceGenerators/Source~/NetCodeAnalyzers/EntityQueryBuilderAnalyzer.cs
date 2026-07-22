using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NetCodeAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class EntityQueryBuilderAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(NetcodeDiagnostics.k_NetC0001Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeEntityQueryBuilder, SyntaxKind.ObjectCreationExpression);
        }

        private void AnalyzeEntityQueryBuilder(SyntaxNodeAnalysisContext context)
        {
            var objectCreation = (ObjectCreationExpressionSyntax)context.Node;

            // 检查是否正在创建 EntityQueryBuilder
            if (!IsEntityQueryBuilderCreation(objectCreation))
                return;

            AnalyzeNetC0001(context, objectCreation);
        }

        private void AnalyzeNetC0001(SyntaxNodeAnalysisContext context, ObjectCreationExpressionSyntax objectCreation)
        {
            // 检查当前调用是否位于预测 System Group 中
            if (!EntitiesHelpers.IsInvocationInPredictionSystemGroup(objectCreation, context))
                return;

            // 查找紧跟在该对象创建表达式之后的方法调用链
            var methodChain = FindEntityQueryBuilderChain(objectCreation);
            if (methodChain.Count == 0)
                return;

            bool hasSimulate = false;
            bool hasIgnoreComponentEnabledState = false;
            Location ignoreLocation = objectCreation.GetLocation();

            foreach (var methodCall in methodChain)
            {
                if (EntitiesHelpers.IsWithSimulate(methodCall, context, false))
                {
                    hasSimulate = true;
                }

                if (EntitiesHelpers.IsWithOptionsIgnoreEnabled(methodCall, ref ignoreLocation))
                {
                    hasIgnoreComponentEnabledState = true;
                }
            }

            if (hasSimulate && hasIgnoreComponentEnabledState)
            {
                context.ReportDiagnostic(Diagnostic.Create(NetcodeDiagnostics.k_NetC0001Descriptor, ignoreLocation));
            }
        }

        private static bool IsEntityQueryBuilderCreation(ObjectCreationExpressionSyntax objectCreation)
        {
            return (objectCreation.Type is TypeSyntax typeSyntax) && typeSyntax.ToString() == "EntityQueryBuilder";
        }

        private List<InvocationExpressionSyntax> FindEntityQueryBuilderChain(ObjectCreationExpressionSyntax objectCreation)
        {
            var chain = new List<InvocationExpressionSyntax>();

            SyntaxNode current = objectCreation;

            while (current.Parent is MemberAccessExpressionSyntax memberAccess &&
                   memberAccess.Parent is InvocationExpressionSyntax invocation)
            {
                chain.Add(invocation);
                current = invocation;
            }

            return chain;
        }
    }
}
