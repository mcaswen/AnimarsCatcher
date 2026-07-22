using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// 存储 Template 克隆的简单缓存
    /// 由代码生成 Context 创建并持有，本身不支持多线程，但每个 SourceGenerator 都具有独立实例
    /// ComponentGenerator 与 CommandGenerator 通过该缓存获取所需 Template 片段
    /// 避免重复读取和解析文本文件
    /// </summary>
    internal class GhostCodeGen
    {
        public override string ToString()
        {
            var replacements = "";
            foreach (var fragment in m_Fragments)
            {
                replacements += $"Key: {fragment.Key}, Template: {fragment.Value.Template}, Content: {fragment.Value.Content}";
            }

            return replacements;
        }

        public Dictionary<string, string> Replacements;
        public Dictionary<string, FragmentData> Fragments => m_Fragments;

        private Dictionary<string, FragmentData> m_Fragments;
        private string m_FileTemplate;
        private string m_HeaderTemplate;
        private CodeGenerator.Context m_Context;
        public class FragmentData
        {
            public string Template;
            public string Content;
        }

        public GhostCodeGen(string template, string templateData, CodeGenerator.Context context)
        {
            m_Context = context;
            ParseTemplate(template, templateData);
        }

        private void ParseTemplate(string templateName, string templateData)
        {
            Replacements = new Dictionary<string, string>();
            m_Fragments = new Dictionary<string, FragmentData>();
            m_HeaderTemplate = "";

            int regionStart;
            // 每个 Template 都以 Header 开始，目前只有 #templateid，因此跳过第一行
            templateData = templateData.Substring(templateData.IndexOf('\n'));
            while ((regionStart = templateData.IndexOf("#region", StringComparison.Ordinal)) >= 0)
            {
                while (regionStart > 0 && templateData[regionStart - 1] != '\n' &&
                       char.IsWhiteSpace(templateData[regionStart - 1]))
                {
                    --regionStart;
                }

                var pre = templateData.Substring(0, regionStart);

                var regionNameEnd = templateData.IndexOf("\n", regionStart, StringComparison.Ordinal);
                var regionNameLine = templateData.Substring(regionStart, regionNameEnd - regionStart);
                var regionNameTokens = System.Text.RegularExpressions.Regex.Split(regionNameLine.Trim(), @"\s+");
                if (regionNameTokens.Length != 2)
                    throw new InvalidOperationException($"Invalid region in GhostCodeGen template '{templateName}', while generating '{m_Context.generatedNs}.{m_Context.generatorName}'.");
                var regionEnd = templateData.IndexOf("#endregion", regionStart, StringComparison.Ordinal);
                if (regionEnd < 0)
                    throw new InvalidOperationException($"Invalid region in GhostCodeGen template '{templateName}', while generating '{m_Context.generatedNs}.{m_Context.generatorName}'.");
                while (regionEnd > 0 && templateData[regionEnd - 1] != '\n' &&
                       char.IsWhiteSpace(templateData[regionEnd - 1]))
                {
                    if (regionEnd <= regionStart)
                        throw new InvalidOperationException($"Invalid region in GhostCodeGen template '{templateName}', while generating '{m_Context.generatedNs}.{m_Context.generatorName}'.");
                    --regionEnd;
                }

                var regionData = templateData.Substring(regionNameEnd + 1, regionEnd - regionNameEnd - 1);
                if (regionNameTokens[1] == "__GHOST_END_HEADER__")
                {
                    m_HeaderTemplate = pre;
                    pre = "";
                }
                else
                {
                    if (m_Fragments.ContainsKey(regionNameTokens[1]))
                    {
                        m_Context.diagnostic.LogError($"The template {templateName} already contains the key [{regionNameTokens[1]}], while generating '{m_Context.generatedNs}.{m_Context.generatorName}'.");
                    }
                    // 这里需要以更灵活的方式自定义字段与 Component 名称
                    // 理想方案是修改 Template 并公开 __GHOST_FIELD_PATH__ 与 __GHOST_REFERENCE_PATH__
                    // 或从 Template 中移除 snapshot.、component.、data. 等固定前缀
                    // 但当前直接修改会破坏使用自定义 Template 的用户项目
                    // 因此采用渐进方案，在处理文本时于内部修补
                    // 目前只需移除固定的 `.`，让代码生成器决定访问器使用点号还是索引器等形式
                    regionData = regionData
                        .Replace(".__GHOST_FIELD_NAME__", "__GHOST_FIELD_PATH__")
                        .Replace(".__GHOST_FIELD_REFERENCE__", "__GHOST_FIELD_REFERENCE__");
                    regionData = regionData.Replace(".__COMMAND_FIELD_NAME__", "__COMMAND_FIELD_NAME__");
                    m_Fragments.Add(regionNameTokens[1], new FragmentData{Template = regionData, Content = ""});
                    pre += regionNameTokens[1];
                }

                regionEnd = templateData.IndexOf('\n', regionEnd);
                var post = "";
                if (regionEnd >= 0)
                    post = templateData.Substring(regionEnd + 1);
                templateData = pre + post;
            }
            if(!m_Fragments.ContainsKey("__GHOST_AGGREGATE_WRITE__"))
                m_Fragments.Add("__GHOST_AGGREGATE_WRITE__", new FragmentData{Template = "", Content = ""});
            m_FileTemplate = templateData;
        }

        private GhostCodeGen()
        {
        }
        public GhostCodeGen Clone()
        {
            var codeGen = new GhostCodeGen();
            codeGen.m_FileTemplate = m_FileTemplate;
            codeGen.m_HeaderTemplate = m_HeaderTemplate;
            codeGen.Replacements = new Dictionary<string, string>();
            codeGen.m_Fragments = new Dictionary<string, FragmentData>();
            codeGen.m_Context = m_Context;
            foreach (var value in m_Fragments)
            {
                codeGen.m_Fragments.Add(value.Key, new FragmentData{Template = value.Value.Template, Content = ""});
            }
            return codeGen;
        }

        private void Validate(string content, string fragment)
        {
            var re = new System.Text.RegularExpressions.Regex(@"(\b__COMMAND\w+)|(\b__GHOST\w+)");
            var matches = re.Matches(content);
            if(matches.Count > 0)
            {
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var name = match.Value;
                    var nameEnd = name.IndexOf("__", 2, StringComparison.Ordinal);
                    if (nameEnd < 0)
                        m_Context.diagnostic.LogError($"Invalid key in GhostCodeGen fragment {fragment} while generating '{m_Context.generatedNs}.{m_Context.generatorName}'.");
                    m_Context.diagnostic.LogError($"GhostCodeGen did not replace {name} in fragment {fragment} while generating '{m_Context.generatedNs}.{m_Context.generatorName}'.");
                }
                throw new InvalidOperationException($"GhostCodeGen failed for fragment {fragment} while generating '{m_Context.generatedNs}.{m_Context.generatorName}'.");
            }
        }

        string Replace(string content, Dictionary<string, string> replacements)
        {
            foreach (var keyValue in replacements)
            {
                content = content.Replace($"__{keyValue.Key}__", keyValue.Value);
            }

            return content;
        }

        public void Append(GhostCodeGen target)
        {
            if (target == null)
                target = this;

            foreach (var fragment in m_Fragments)
            {
                if (!target.m_Fragments.ContainsKey($"{fragment.Key}"))
                {
                    m_Context.diagnostic.LogError($"Target CodeGen is missing fragment '{fragment.Key}' while generating '{m_Context.generatedNs}.{m_Context.generatorName}'.");
                    continue;
                }
                target.m_Fragments[$"{fragment.Key}"].Content += m_Fragments[$"{fragment.Key}"].Content;
            }
        }

        public void AppendFragment(string fragment,
            GhostCodeGen target, string targetFragment = null, string extraIndent = null)
        {
            if (target == null)
                target = this;
            if (targetFragment == null)
                targetFragment = fragment;
            if (!m_Fragments.ContainsKey($"__{fragment}__"))
                throw new InvalidOperationException($"Generating '{m_Context.generatedNs}.{m_Context.generatorName}', '{fragment}' is not a valid fragment in the given template.");
            if (!target.m_Fragments.ContainsKey($"__{targetFragment}__"))
                throw new InvalidOperationException($"Generating '{m_Context.generatedNs}.{m_Context.generatorName}', '{targetFragment} is not a valid fragment in the given template.");

            target.m_Fragments[$"__{targetFragment}__"].Content += m_Fragments[$"__{fragment}__"].Content;
        }

        public string GetFragmentTemplate(string fragment)
        {
            if (!m_Fragments.ContainsKey($"__{fragment}__"))
                throw new InvalidOperationException($"Generating '{m_Context.generatedNs}.{m_Context.generatorName}', cannot get fragment template, as fragment '{fragment}' is not found.");
            return m_Fragments[$"__{fragment}__"].Template;
        }
        public string GetFragmentContent(string fragment)
        {
            if (!m_Fragments.ContainsKey($"__{fragment}__"))
                throw new InvalidOperationException($"Generating '{m_Context.generatedNs}.{m_Context.generatorName}', cannot get fragment template, as fragment '{fragment}' is not found.");
            return m_Fragments[$"__{fragment}__"].Content;
        }

        public bool HasFragment(string fragment)
        {
            return m_Fragments.ContainsKey($"__{fragment}__");
        }

        public bool GenerateFragment(string fragment, Dictionary<string, string> replacements,
            GhostCodeGen target = null, string targetFragment = null, string extraIndent = null, bool allowMissingFragment = false,
            bool prepend = false)
        {
            if (target == null)
                target = this;
            if (targetFragment == null)
                targetFragment = fragment;
            if (!m_Fragments.ContainsKey($"__{fragment}__"))
            {
                if (allowMissingFragment)
                    return false;
                throw new InvalidOperationException($"{fragment} is not a valid fragment for the given template! replacements: [{(replacements != null ? string.Join(",",replacements) : null)}]!");
            }
            if (!target.m_Fragments.ContainsKey($"__{targetFragment}__"))
                throw new InvalidOperationException($"{targetFragment} is not a valid targetFragment for the given template! replacements: [{(replacements != null ? string.Join(",",replacements) : null)}]!");
            var content = Replace(m_Fragments[$"__{fragment}__"].Template, replacements);

            if (extraIndent != null)
                content = extraIndent + content.Replace("\n    ", $"\n    {extraIndent}");

            Validate(content, fragment);
            if (prepend)
                target.m_Fragments[$"__{targetFragment}__"].Content = content + target.m_Fragments[$"__{targetFragment}__"].Content;
            else
                target.m_Fragments[$"__{targetFragment}__"].Content += content;
            return true;
        }

        public void ReplaceContentInFragments(string[] fragments, string value, string replacement)
        {
            foreach (var key in fragments)
            {
                m_Fragments[$"__{key}__"].Content = m_Fragments[$"__{key}__"].Content.Replace(value, replacement);
            }
        }

        /// <summary>
        /// 渲染 Template 并将生成文件加入当前批次
        /// </summary>
        /// <param name="generatorName">生成文件名称</param>
        /// <param name="replacements">Template 替换项</param>
        /// <param name="batch">接收生成文件的批次</param>
        public void GenerateFile(
            string generatorName,
            Dictionary<string, string> replacements, List<CodeGenerator.GeneratedFile> batch)
        {
            var content = GenerateContent(replacements);
            batch.Add(new CodeGenerator.GeneratedFile
            {
                GeneratedFileName = generatorName,
                Code = content
            });
        }

        /// <summary>
        /// 输出全部片段并应用所有替换项，将 Template 渲染为字符串
        /// </summary>
        /// <param name="replacements"></param>
        /// <returns></returns>
        public string GenerateContent(Dictionary<string, string> replacements)
        {
            var header = Replace(m_HeaderTemplate, replacements);
            var content = Replace(m_FileTemplate, replacements);

            foreach (var keyValue in m_Fragments)
            {
                header = header.Replace(keyValue.Key, keyValue.Value.Content);
                content = content.Replace(keyValue.Key, keyValue.Value.Content);
            }
            content = header + content;
            Validate(content, "Root");
            return content;
        }
    }
}
