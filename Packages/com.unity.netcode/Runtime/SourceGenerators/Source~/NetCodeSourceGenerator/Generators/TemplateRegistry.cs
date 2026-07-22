using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// 导入并验证全部 NetCode Template 文件，再将其提供给代码生成系统
    /// </summary>
    internal class TemplateRegistry
    {
        const string k_TemplateId = "#templateid:";
        public readonly Dictionary<TypeDescription, TypeTemplate> TypeTemplates = new (16);
        private readonly Dictionary<string, SourceText> allTemplates = new (16);
        private readonly IDiagnosticReporter diagnostic;

        public TemplateRegistry(IDiagnosticReporter diagnosticReporter)
        {
            diagnostic = diagnosticReporter;
        }

        public void AddTypeTemplates(IEnumerable<TypeRegistryEntry> types)
        {
            foreach (var entry in types)
            {
                AddTypeTemplateEntry(entry);
            }
        }

        private void AddTypeTemplateEntry(in TypeRegistryEntry entry)
        {
            var typeDescription = new TypeDescription
            {
                TypeFullName = entry.Type,
                Key = entry.Type,
                Attribute = new TypeAttribute
                {
                    subtype = entry.SubType,
                    quantization = entry.Quantized ? 1 : -1,
                    smoothing = (uint)entry.Smoothing,
                    aggregateChangeMask = entry.Composite
                }
            };
            var template = new TypeTemplate
            {
                SupportsQuantization = entry.Quantized,
                Composite = entry.Composite,
                SupportCommand = entry.SupportCommand,
                TemplatePath = entry.Template,
                TemplateOverridePath = entry.TemplateOverride
            };
            TypeTemplates.Add(typeDescription, template);
        }

        public string FormatAllKnownTypes()
        {
            return $"[{TypeTemplates.Count}:{string.Join(",", TypeTemplates.Keys)}]";
        }

        public string FormatAllKnownSubTypes()
        {
            var aggregate = string.Join(",", TypeTemplates
                .Where(x => x.Key.Attribute.subtype != 0)
                .Select(x => $"[{x.Key.Attribute.subtype}: {x.Key} at {x.Value.TemplatePath}]"));
            return $"[{TypeTemplates.Count}:{aggregate}]";
        }

        /// <summary>
        /// 解析传入 Compilation 的 Additional File，并将自定义 Template 加入内部 Map
        /// 有效 Template 文件必须以 `.netcode.additionalfile` 为扩展名
        /// 且首行必须以 `#templateid: TEMPLATE_ID` 开头
        /// </summary>
        /// <param name="additionalFiles">传入 Compilation 的 Additional File</param>
        /// <param name="typeRegistryEntries">类型 Template 注册条目</param>
        /// <param name="generatorTemplates">生成器内部必需的 Template 标识</param>
        public void AddAdditionalTemplates(ImmutableArray<AdditionalText> additionalFiles,
            List<TypeRegistryEntry> typeRegistryEntries, HashSet<string> generatorTemplates)
        {
            var missingUserTypes = new List<TypeRegistryEntry>(typeRegistryEntries);
            var templateIds = new Dictionary<string, AdditionalText>(additionalFiles.Length);

            foreach (var additionalText in additionalFiles)
            {
                var isNetCodeTemplate = additionalText.Path.EndsWith(NetCodeSourceGenerator.NETCODE_ADDITIONAL_FILE, StringComparison.Ordinal);
                if (isNetCodeTemplate)
                {
                    var text = additionalText.GetText();
                    if (text == null || text.Lines.Count == 0)
                    {
                        diagnostic.LogError($"All NetCode AdditionalFiles must be valid Templates, but '{additionalText.Path}' does not contain any text!");
                        continue;
                    }

                    var line = text.Lines[0].ToString();
                    if (!line.StartsWith(k_TemplateId, StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostic.LogError($"All NetCode AdditionalFiles must be valid Templates, but '{additionalText.Path}' does not start with a correct Template definition (a '#templateid:MyNamespace.MyType' line).");
                        continue;
                    }

                    var templateId = line.Substring(k_TemplateId.Length).Trim();
                    if (string.IsNullOrWhiteSpace(templateId))
                    {
                        diagnostic.LogError($"NetCode AdditionalFile '{additionalText.Path}' is a valid Template, but the `{k_TemplateId}` is empty!");
                        continue;
                    }
                    templateIds.Add(templateId, additionalText);
                }
                else
                {
                    diagnostic.LogDebug($"Ignoring AdditionalFile '{additionalText.Path}' as it is not a NetCode type!");
                }
            }

            foreach (var generatorTemplate in generatorTemplates)
            {
                if (!templateIds.TryGetValue(generatorTemplate, out var file))
                    diagnostic.LogError($"Missing internal Netcode package template {generatorTemplate}!");
                else
                {
                    templateIds.Remove(generatorTemplate);
                    allTemplates.Add(generatorTemplate, file.GetText());
                }
            }
            var unusedTemplates = new Dictionary<string, AdditionalText>(templateIds);
            // 确保每个 TypeRegistryEntry 都能关联到 Additional File Template
            foreach (var typeRegistryEntry in typeRegistryEntries)
            {
                if (!string.IsNullOrEmpty(typeRegistryEntry.Template))
                {
                    if(!templateIds.TryGetValue(typeRegistryEntry.Template, out var file))
                    {
                        diagnostic.LogError($"Unable to find the `Template` associated with '{typeRegistryEntry}'. There are {additionalFiles.Length} additionalFiles:[{string.Join(",", additionalFiles.Select(x => x.Path))}]!");
                    }
                    else
                    {
                        unusedTemplates.Remove(typeRegistryEntry.Template);
                        if(!allTemplates.ContainsKey(typeRegistryEntry.Template))
                            allTemplates.Add(typeRegistryEntry.Template, file.GetText());}
                }

                if (!string.IsNullOrEmpty(typeRegistryEntry.TemplateOverride))
                {
                    if(!templateIds.TryGetValue(typeRegistryEntry.TemplateOverride, out var file))
                    {
                        diagnostic.LogError($"Unable to find the `TemplateOverride` associated with '{typeRegistryEntry}'. There are {additionalFiles.Length} additionalFiles:[{string.Join(",", additionalFiles.Select(x => x.Path))}]!");
                    }
                    else
                    {
                        unusedTemplates.Remove(typeRegistryEntry.TemplateOverride);
                        if(!allTemplates.ContainsKey(typeRegistryEntry.TemplateOverride))
                            allTemplates.Add(typeRegistryEntry.TemplateOverride, file.GetText());}
                }
            }

            // 确保没有 Additional File 无法匹配任何 Template 定义，这更接近警告而非错误
            foreach(var missingMatch in unusedTemplates)
                diagnostic.LogError($"NetCode AdditionalFile '{missingMatch.Value.Path}' (named '{missingMatch.Key}') is a valid Template, but it cannot be matched with any Netcode package or UserDefinedTemplate template definition (probably a typo). Known user templates:[{GetKnownCustomUserTemplates()}].");

            string GetKnownCustomUserTemplates()
            {
                return string.Join(",", typeRegistryEntries.Select(x => $"{x.Type}[{x.Template}]"));
            }
        }


        /// <summary>
        /// 获取指定 Template 标识对应的文本数据
        /// </summary>
        /// <param name="resourcePath">Template 资源标识</param>
        /// <returns>
        /// Template 文本内容
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// 无法解析 Template 路径或标识时抛出
        /// </exception>
        public string GetTemplateData(string resourcePath)
        {
            if (allTemplates.TryGetValue(resourcePath, out var additionalText))
                return additionalText.ToString();

            throw new FileNotFoundException($"Cannot find template with resource id '{resourcePath}'! CustomTemplates:[{string.Join(",", allTemplates)}]");
        }

        private Stream LoadTemplateFromEmbeddedResources(string resourcePath)
        {
            // 嵌入资源中的 Template 名称以命名空间开头
            var thisAssembly = Assembly.GetExecutingAssembly();
            return thisAssembly.GetManifestResourceStream(resourcePath);
        }
    }
}
