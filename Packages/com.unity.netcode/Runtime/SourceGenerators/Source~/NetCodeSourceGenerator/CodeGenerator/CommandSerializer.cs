using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Unity.NetCode.Generators
{
    // CommandSerializer 实例由 CodeGenerator 创建，此类本身不是线程安全的
    // 但每个 SourceGenerator 都具有独立 Context，因此可以安全使用
    // 此处应避免共享静态变量或状态；确有需要时，必须保证它们不可变或线程安全
    internal class CommandSerializer
    {
        public enum Type
        {
            Rpc,
            Command,
            Input
        }
        private readonly TypeInformation m_TypeInformation;
        private GhostCodeGen m_CommandGenerator;
        private readonly TypeTemplate m_Template;

        public Type CommandType { get; }

        public GhostCodeGen CommandGenerator => m_CommandGenerator;

        public CommandSerializer(CodeGenerator.Context context, Type t)
        {
            CommandType = t;
            string template = String.Empty;
            switch (t)
            {
                case Type.Rpc:
                    template = CodeGenerator.RpcSerializer;
                    break;
                case Type.Command:
                    template = CodeGenerator.CommandSerializer;
                    break;
                case Type.Input:
                    template = CodeGenerator.InputSynchronization;
                    break;
            }
            var generator = context.codeGenCache.GetTemplate(template);
            m_CommandGenerator = generator.Clone();
        }
        public CommandSerializer(CodeGenerator.Context context, Type t, TypeInformation information) : this(context, t)
        {
            m_TypeInformation = information;
        }

        public CommandSerializer(CodeGenerator.Context context, Type t, TypeInformation information, TypeTemplate template) : this(context, t)
        {
            m_TypeInformation = information;
            m_Template = template;
        }

        public void AppendTarget(CommandSerializer typeSerializer)
        {
            m_CommandGenerator.Append(typeSerializer.m_CommandGenerator);
        }

        public void GenerateFixedListField(CodeGenerator.Context context,
            CommandSerializer fixedListArgGen,
            TypeInformation typeInformation,
            string rootPath=null, Dictionary<string, string> replacements=null)
        {
            // 需要创建辅助类型来序列化列表，因为变量名替换、向其他 Template 追加子片段等操作并不方便
            // 增加一层间接结构可以简化整体代码生成
            var argumentHelperName = $"{context.generatedNs}_{fixedListArgGen.m_TypeInformation.TypeFullName}_CmdSerializer".Replace('.', '_');
            var fixedListHelperGenerator = context.codeGenCache.GetTemplate(CodeGenerator.GhostFixedListCommandHelper).Clone();
            // 此处还必须补充必要的 using，否则生成代码无法正确解析类型
            fixedListHelperGenerator.Replacements["GHOST_NAME"] = context.root.TypeFullName;
            fixedListHelperGenerator.Replacements["GHOST_COMMAND_HELPER_NAME"] = argumentHelperName;
            fixedListHelperGenerator.Replacements["COMMAND_FIXEDLIST_CAP"] = typeInformation.ElementCount.ToString();
            fixedListHelperGenerator.Replacements["COMMAND_FIXEDLIST_LEN_BITS"] = (32-CodeGenerator.lzcnt((uint)typeInformation.ElementCount)).ToString();
            fixedListHelperGenerator.Replacements["GHOST_NAMESPACE"] = context.generatedNs;
            fixedListHelperGenerator.Replacements["COMMAND_COMPONENT_TYPE"] = fixedListArgGen.m_TypeInformation.FieldTypeName;
            if (!context.generatedTypes.Contains(argumentHelperName))
            {
                fixedListArgGen.CommandGenerator.AppendFragment("COMMAND_READ", fixedListHelperGenerator);
                fixedListArgGen.CommandGenerator.AppendFragment("COMMAND_WRITE", fixedListHelperGenerator);
                if (CommandType != Type.Rpc)
                {
                    fixedListArgGen.CommandGenerator.AppendFragment("COMMAND_READ_PACKED", fixedListHelperGenerator);
                    fixedListArgGen.CommandGenerator.AppendFragment("COMMAND_WRITE_PACKED", fixedListHelperGenerator);
                    fixedListArgGen.CommandGenerator.AppendFragment("GHOST_COMPARE_INPUTS", fixedListHelperGenerator, "GHOST_COMPARE_INPUTS");
                }
                fixedListHelperGenerator.GenerateFile(argumentHelperName + "_CommandHelper.cs", fixedListHelperGenerator.Replacements, context.batch);
                context.generatedTypes.Add(argumentHelperName);
            }
            GenerateFields(context, rootPath, typeInformation, fixedListHelperGenerator.Replacements);
        }

        public void GenerateFields(CodeGenerator.Context context,
            string rootPath,
            TypeInformation typeInformation,
            Dictionary<string, string> replacements = null)
        {
            if (m_Template == null)
                return;

            var generator = context.codeGenCache.GetTemplateWithOverride(m_Template.TemplatePath, m_Template.TemplateOverridePath);
            generator = generator.Clone();

            if (CommandType == Type.Input)
            {
                // 为 Input 结构体内的 InputEvent 类型生成递增与递减片段
                // 实际处理的是嵌套于父级 InputEvent 结构体中的整数计数字段
                if (m_TypeInformation.ContainingTypeFullName == "Unity.NetCode.InputEvent")
                {
                    m_CommandGenerator.Replacements.Add("EVENTNAME", m_TypeInformation.FieldPath);
                    m_CommandGenerator.GenerateFragment("INCREMENT_INPUTEVENT", m_CommandGenerator.Replacements, m_CommandGenerator);
                    m_CommandGenerator.GenerateFragment("DECREMENT_INPUTEVENT", m_CommandGenerator.Replacements, m_CommandGenerator);
                }
                // 其余字段由 Command Template 处理，无需继续生成
                return;
            }

            if (replacements == null)
                replacements = generator.Replacements;

            string fieldPath = null;
            if (string.IsNullOrEmpty(typeInformation.FieldPath))
                fieldPath = $"{typeInformation.FieldName}";
            else
                fieldPath = $"{typeInformation.FieldPath}.{typeInformation.FieldName}";
            fieldPath = fieldPath.Trim();
            var fieldAccessor = string.IsNullOrEmpty(fieldPath) ? "" : ".";

            replacements["COMMAND_FIELD_NAME"] = $"{rootPath}{fieldAccessor}{fieldPath}";
            replacements["COMMAND_FIELD_TYPE_NAME"] = m_TypeInformation.FieldTypeName;
            generator.GenerateFragment("COMMAND_READ", replacements, m_CommandGenerator);
            generator.GenerateFragment("COMMAND_WRITE", replacements, m_CommandGenerator);
            if (CommandType != Type.Rpc)
            {
                generator.GenerateFragment("COMMAND_READ_PACKED", replacements, m_CommandGenerator);
                generator.GenerateFragment("COMMAND_WRITE_PACKED", replacements, m_CommandGenerator);
                if (!m_TypeInformation.CanBatchPredict)
                {
                    generator.Replacements.Add("GHOST_MASK_INDEX", "0");
                    generator.Replacements.Add("GHOST_FIELD_NAME", fieldPath);
                    generator.Replacements.Add("GHOST_FIELD_PATH", $"{rootPath}{fieldAccessor}{fieldPath}");
                    generator.Replacements.Add("GHOST_FIELD_TYPE_NAME", typeInformation.FieldTypeName);
                    if(generator.HasFragment("GHOST_CALCULATE_INPUT_CHANGE_MASK"))
                        generator.GenerateFragment("GHOST_CALCULATE_INPUT_CHANGE_MASK", replacements, m_CommandGenerator, "GHOST_COMPARE_INPUTS");
                    else
                        generator.GenerateFragment("GHOST_CALCULATE_CHANGE_MASK", replacements, m_CommandGenerator, "GHOST_COMPARE_INPUTS");
                }
            }
        }

        public void GenerateSerializer(CodeGenerator.Context context, TypeInformation typeInfo)
        {
            var typeFullName = typeInfo.TypeFullName.Replace('+', '.');
            var displayName = typeFullName.Replace("Unity.NetCode.InputBufferData<", "").Replace(">", ""); // 这里通过字符串处理移除泛型包装名称
            var replacements = new Dictionary<string, string>
            {
                {"COMMAND_NAME", context.generatorName.Replace(".", "").Replace('+', '_')},
                {"COMMAND_NAMESPACE", context.generatedNs},
                {"COMMAND_COMPONENT_TYPE", typeFullName},
                {"COMMAND_COMPONENT_TYPE_DISPLAY_NAME", CodeGenerator.SmartTruncateDisplayNameForFs64B(displayName)},
            };

            if (!string.IsNullOrEmpty(typeInfo.Namespace))
                context.imports.Add(typeInfo.Namespace);

            foreach (var ns in context.imports)
            {
                replacements["COMMAND_USING"] = CodeGenerator.GetValidNamespaceForType(context.generatedNs, ns);
                m_CommandGenerator.GenerateFragment("COMMAND_USING_STATEMENT", replacements);
            }

            var serializerName = context.generatedFilePrefix + "CommandSerializer.cs";
            m_CommandGenerator.GenerateFile(serializerName, replacements, context.batch);
        }

        public override string ToString()
        {
            var debugInformation = m_TypeInformation.ToString();
            debugInformation += m_Template?.ToString();
            debugInformation += m_CommandGenerator?.ToString();
            return debugInformation;
        }
    }
}
