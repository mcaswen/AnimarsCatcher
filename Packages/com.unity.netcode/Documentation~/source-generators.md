# Netcode for Entities 源码生成器

Netcode for Entities 包使用 Roslyn Source Generator，在编译时自动生成以下内容：

- 复制组件、缓冲区、`ICommandData`、RPC 和 `IInputCommandData` 的全部序列化代码
- 处理 RPC 和命令所需的全部样板系统
- 在 `IInputCommandData` 与底层 `ICommandData` 缓冲区之间复制数据的系统
- 其他内部系统，主要用于注册复制类型
- 从复制类型提取全部信息，避免运行时使用反射

项目结构如下：

```text
Unity.NetCode
- Editor
- Runtime
  -- SourceGenerators       标签
  --- NetCodeGenerator.dll  *SourceGenerator*
  ---- Source~  （隐藏，不由 Unity 处理）
  ------ NetCodeSourceGenerator
  ------- CodeGenerator
  ------- Generators
  ------- Helpers
  ------ Tests
  ------ SourceGenerators.sln
```

`NetCodeSourceGenerator.dll` 由 `Source~` 文件夹生成，并由编辑器编译管线使用，将生成代码注入各程序集定义以及 `Assembly-CSharp` 等程序集

> [!IMPORTANT]
> 生成器 DLL 比较特殊，具有以下明确要求：
>
> 1. Unity 编辑器或任何平台都**不能**导入它，因为它们不兼容
> 2. 为了让编译管线检测到它并将其作为生成器使用，DLL **必须**带有 `SourceGenerator` 标签

包内 DLL 已经正确配置。不过，重新编译 DLL 后如果部分设置丢失，可以通过编辑器重新设置、编辑 `.meta` 文件，或恢复先前的 `.meta` 文件

<a id="generator-output"></a>
## 生成器输出

默认情况下，Netcode 生成器会将全部生成文件输出到 `Temp/NetcodeGenerated` 文件夹，也可以通过 Multiplayer 菜单快捷方式访问该目录
生成器会为每个产生了序列化代码的程序集创建一个子文件夹

生成器还会将全部信息和调试日志写入 `Temp/NetcodeGenerated/sourcegenerator.log`。错误和警告也会输出到编辑器 Console

<a id="configuring-the-files-and-logging-generator-behaviour"></a>
## 配置生成文件与日志行为

可以使用 Roslyn Analyzer 配置文件设置生成器。Unity 2022 及更高版本可以检测 `GlobalAnalyzerConfig` 资源；配置既可以放在 `Assets` 根目录作为全局配置，也可以像 `.buildrule` 文件一样按程序集定义设置

若要配置传给 Netcode 生成器的选项，需要在项目的 `Assets` 文件夹中创建 `Default.globalconfig` 文本文件<br/>
该文件必须包含键值对列表，格式如下：

```ini
# 可以这样写注释
is_global=true

your_key=your value
your_key=your value
...
```

有关格式和 Analyzer 配置的详细信息，请参阅 Microsoft 的 [Global Analyzer Config 文档](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/configuration-files#global-analyzerconfig)

Netcode 生成器支持以下标志和键：

| 键 | 值 | 说明 |
|----|----|------|
| `unity.netcode.sourcegenerator.outputfolder` | 有效的相对路径字符串 | 覆盖生成器写入日志和生成文件的输出目录。路径应相对于项目目录，默认为 `Temp/NetCodeGenerated` |
| `unity.netcode.sourcegenerator.write_files_to_disk` | 空或 1 表示启用，0 表示禁用 | 启用或禁用将生成文件写入输出目录 |
| `unity.netcode.sourcegenerator.write_logs_to_disk` | 空或 1 表示启用，0 表示禁用 | 启用或禁用将日志写入输出目录。禁用后，全部日志都会重定向到编辑器日志 |
| `unity.netcode.sourcegenerator.emit_timing` | 空或 1 表示启用，0 表示禁用 | 记录每个已编译程序集的耗时信息 |
| `unity.netcode.sourcegenerator.logging_level` | `info`、`warning` 或 `error` | 设置日志级别，**默认为 `error`** |
| `unity.netcode.sourcegenerator.attach_debugger` | 可选程序集名称 | 暂停生成器执行并等待调试器附加。如果程序集名称非空，生成器只在处理该程序集时等待调试器 |

<a id="how-to-build-the-source-generators"></a>
## 构建源码生成器

某些情况下可能需要重新编译包内生成器，例如修复问题或扩展功能

生成器 DLL 必须在 Unity 外部使用 [.NET SDK 6.0](https://dotnet.microsoft.com/en-us/download/dotnet/6.0) 或更高版本手动编译。可以在命令提示符中进入 `Packages\com.unity.netcode\Runtime\SourceGenerators\Source~` 目录，再执行 dotnet 命令

使用 `dotnet publish -c Release` 编译 Release 构建<br/>
使用 `dotnet publish -c Debug` 编译 Debug 构建，调试时推荐使用

也可以使用提供的 **Packages/com.unity.netcode/Runtime/SourceGenerators/Source~/SourceGenerators.sln** 解决方案构建和调试

<a id="how-to-debug-generator-problems"></a>
## 调试生成器问题

源码生成器的调试起初可能比较困难。生成器由外部进程调用，必须将调试器附加到该进程才能单步执行代码

第一步是在 Rider 或 Visual Studio 中打开 `SourceGenerators.sln`，并使用 [**Debug 配置**](#how-to-build-the-source-generators)重新编译生成器

为简化生成器调用时附加调试器的过程，本包提供了一些工具，可以在可控时机附加到运行中的进程

<a id="using-the-global-config"></a>
### 使用全局配置

添加 `unity.netcode.sourcegenerator.attach_debugger` 选项后，可以让生成器在每次调用时，或只在处理特定程序集时等待调试器附加

<a id="modify-the-generator-code"></a>
### 修改生成器代码

可以使用 `Debug.LaunchDebugger` 辅助方法：

```csharp
// 无条件启动调试器
Debug.LaunchDebugger();
// 当前处理的程序集名称匹配时启动调试器
Debug.LaunchDebugger(GeneratorExecutionContext context, string assembly);
```

这些辅助方法可以从任意位置调用。建议先在 `NetcodeSourceGenerator.cs` 的 `Execute` 方法中调用

```csharp
public void Execute(GeneratorExecutionContext executionContext)
{
    ....
    Debug.LaunchDebugger();
    try
    {
        Generate(executionContext, diagnostic);
    }
    catch (Exception e)
    {
       ...
    }
}
```

> [!NOTE]
> `Execute` 会为每个程序集调用一次。如果未使用程序集过滤器，屏幕上会出现多个弹窗

无论采用哪种方式，系统都会在适当时机打开对话框，其中会显示需要附加到的进程 ID
