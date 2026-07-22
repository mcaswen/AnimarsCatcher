using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Unity.NetCode.Generators
{
    internal class NetCodeSyntaxReceiver : ISyntaxReceiver
    {
        readonly public List<SyntaxNode> Variants;
        readonly public List<SyntaxNode> Candidates;

        public NetCodeSyntaxReceiver()
        {
            Variants = new List<SyntaxNode>();
            Candidates = new List<SyntaxNode>();
        }

        ///<summary>
        /// 分析全部语法节点，并构建 RPC、Command 与 Component 候选列表
        /// 类型成为潜在候选的最低要求为：
        /// - 必须是结构体
        /// - 结构体声明必须为 public
        /// - 必须实现 RpcCommandData、ICommandData、ComponentData 或 IBufferElementData 之一
        /// 此阶段不检查 Ghost Field，因为所需 Ghost Modifier 尚不可用
        /// 后续可通过 Unity Editor 中的正式配置文件完善该工作流
        ///
        /// 当前检查存在限制，因为在语法层无法判断接口继承
        /// 如果 Component 实现了继承自 IBufferElementData 或 IComponentData 的接口
        /// 当前逻辑无法直接判断其类型或分类
        ///
        /// 可改为在此收集至少实现一个接口的全部结构体
        /// 再在第二轮通过语义模型执行正确检查，所需辅助能力已经具备
        ///</summary>
        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            using (new Profiler.Auto("OnVisitSyntaxNode"))
            {
                if (!(syntaxNode is StructDeclarationSyntax))
                {
                    // 节点必须是结构体，或具有 [GhostComponent] 特性的类
                    if (!(syntaxNode is ClassDeclarationSyntax))
                        return;
                    if (!ComponentFactory.HasGhostComponentAttribute((TypeDeclarationSyntax)syntaxNode))
                        return;
                }

                var structNode = (TypeDeclarationSyntax) syntaxNode;

                if(structNode.TypeParameterList != null)
                    return;

                // 检查 Variant 特性
                if (structNode.AttributeLists.Count > 0)
                {
                    var attributes = structNode.AttributeLists.SelectMany(list => list.Attributes.Select(a =>
                            (a.Name.IsKind(SyntaxKind.QualifiedName) ? ((QualifiedNameSyntax) a.Name).Right : a.Name)
                            .ToString()));

                    if (attributes.Any(attr => attr == "GhostComponentVariation" || attr == "GhostComponentVariationAttribute"))
                    {
                        Variants.Add(structNode);
                        return;
                    }
                }

                if (structNode.BaseList == null || structNode.BaseList.Types.Count == 0)
                    return;

                using (new Profiler.Auto("Collect"))
                {
                    bool shouldAddType = true;
                    foreach (var b in structNode.BaseList.Types)
                    {
                        var interfaceType = b.Type;
                        // 移除限定名
                        if(interfaceType.IsKind(SyntaxKind.QualifiedName))
                            interfaceType = ((QualifiedNameSyntax)interfaceType).Right;

                        if (interfaceType.IsKind(SyntaxKind.GenericName))
                        {
                            if (((GenericNameSyntax)interfaceType).TypeArgumentList.Arguments.Count == 0)
                            {
                                shouldAddType = false;
                                break;
                            }
                        }
                        else if (!interfaceType.IsKind(SyntaxKind.IdentifierName))
                        {
                            shouldAddType = false;
                            break;
                        }
                    }
                    if(shouldAddType)
                        Candidates.Add(structNode);
                }
            }
        }
    }
}
