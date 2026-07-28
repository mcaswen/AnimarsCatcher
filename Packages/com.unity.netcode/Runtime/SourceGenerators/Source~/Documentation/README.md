# Netcode 代码生成器简介

<a id="the-motivation-for-moving-toward-a-new-workflow"></a>
## 迁移到新工作流的原因

旧代码生成系统依赖 Unity 编辑器编译钩子，将生成的序列化代码添加到项目。简化来说，每个引用 `Unity.NetCode` 且包含待序列化类型的程序集编译完成后，系统都会生成：

- 一个新的派生程序集定义，后缀为 `.generated`
- 全部组件、RPC 和命令序列化器
- 一个负责在运行时注册这些序列化器的系统

这套逻辑虽然能够工作，但高度定制化，与 DOTS 其他位置的代码生成方式不同，并且存在一些边界问题。更重要的是，它会延长编译时间；大型项目尤其明显，因为需要编译更多程序集

新代码生成工作流的第一阶段目标是：

* 采用更接近 Unity.Entities 包的方式，统一生成代码的注入方式
* 在第一次编译过程中直接添加代码，缩短编译时间
* 移除额外生成的程序集
* 简化代码生成逻辑

此外，还希望带来以下收益：

* 以更简单且安全的方式向序列化生成流程添加或移除自定义类型
* 提供更易用且安全的接口来添加或移除 Ghost 变体
* 自动向项目添加自定义 SubType
* 为无法直接访问的类型注入序列化代码，例如物理或 Transform 类型

<a id="the-new-logic-welcome-to-sourcegenerators"></a>
## 新逻辑：Source Generator

新方案使用 Microsoft 新增、Entities 已用于替代 IL 后处理部分能力的 Source Generator。后处理仍然存在，但只用于一两个特定场景

<a id="sourcegenerators-sg-for-brevity-in-a-nutshell"></a>
### Source Generator 简述

Source Generator，以下简称 SG，允许分析抽象语法树 AST，类似 Roslyn Analyzer，并向当前编译过程**添加**新的编译单元，也就是 C# 文件

通过导入 Unity.Roslyn 包提供的自定义编译器启用 SG。安装该包后，项目中包括各 Package 内所有带有 `SourceGenerator` 标签的动态库，都会作为 Analyzer 加入当前编译过程

若要创建源码生成器，需要实现 `ISourceGenerator` 接口，并为类添加 `[SourceGenerator]` 特性，使 Roslyn 编译器能够检测和使用它

![Source Generator 示意图](diagram.png)

所有生成器通过一次或两次过程分析代码：

* 第一阶段，可选：解析语法树，找出可能需要生成代码的候选结构体、类、方法或其他节点
* 第二阶段，生成：使用候选节点或直接查找特定类型，解析语义类型模型，获取特性、类型名称、接口等常规类型信息，再向 `SourceGeneratorContext` 添加有效或无效的 C# 代码

当前实现分两步生成代码：

1. 分析语法树，查找结构体，并按照以下条件筛选候选类型
    * **实现以下接口之一**
        * `IBufferElementData`
        * `IComponentData`
        * `IRpcCommandData`
        * `ICommandData`
    * **具有以下特性之一**
        * `GhostComponentVariation`
2. 使用语法分析阶段找到的候选类型生成以下内容
    * 为每个组件生成序列化器
    * 生成注册系统

为减少潜在问题，并在无需重写整套测试的情况下确保生成代码正常工作，当前实现复用了几乎全部纯代码生成和模板逻辑，只移除了对 Cecil 与反射的依赖，使这些逻辑真正独立。这样只要提取出的类型树正确，就能保证最终生成与旧系统相同的代码

<a id="source-generator-for-devs"></a>
## 面向开发者的源码生成器说明

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

`NetCodeSourceGenerator.dll` 是特殊 DLL，其中包含 Netcode 的 Source Generator 实现，并由 csc 编译器使用

Unity **不得**在任何平台或编辑器中导入该 DLL。它依赖 Roslyn 和部分 Microsoft DLL，与 Unity 导入环境不兼容；此外，Netcode 与源码生成器之间共享部分类型和源码，导入后会发生冲突并导致导入错误

为了让 `ExternalCSharpCompiler`，即 Roslyn 包检测生成器 DLL，必须为其添加 `SourceGenerator` 标签

有两种添加方式：

- 手动编辑 `.meta` 文件，在文件开头添加以下部分

  ```yaml
  labels:
  - SourceGenerator
  ```

- 通过编辑器添加标签，更安全。受一个限制或缺陷影响，过程略显繁琐
  - 先将 DLL 移到 `Assets` 文件夹
  - 在该位置添加标签
  - 最后将 DLL 移回 Package 文件夹

包内已经完成标签设置，无需额外处理

`GeneratorShared.dll` 包含 Netcode 与源码生成器之间共享的全部代码，也必须添加 `SourceGenerator` 标签。这样可以更简单地找到全部类型，无需动态解析

<a id="how-to-build-source-generators"></a>
#### 构建源码生成器

源码生成器 DLL 必须在 Unity 编译管线外使用 [.NET SDK 6.0](https://dotnet.microsoft.com/en-us/download/dotnet/6.0) 或更高版本手动编译

可以在命令提示符中进入 `Packages\com.unity.netcode\Runtime\SourceGenerators\Source~` 目录，然后执行：

```powershell
dotnet publish -c Release
```

也可以使用同一文件夹内的 `SourceGenerators.sln` 解决方案构建和调试。若要调试源码生成器，执行 publish 命令时将 `Release` 替换为 `Debug`

<a id="how-to-unit-test-generators"></a>
#### 对生成器执行单元测试

除了源码生成器 DLL，项目还配置了测试项目。现在无需运行或编译编辑器即可对 SG 逻辑和代码执行单元测试与调试，这会加快开发迭代，也便于分析特定用例

可以使用 `dotnet test` 运行测试，Rider 和 Visual Studio 也能正常发现并运行测试

项目已经提供一组覆盖生成流程多个方面的单元测试。请在 `SourceGeneratorTests.cs` 中添加其他测试，或在 `Tests` 文件夹中创建新文件

<a id="how-to-debug-generators"></a>
#### 调试生成器

调试源码生成器可能有些繁琐，但可以实现

如果能够在 Windows 上运行测试并使用 Visual Studio，建议优先采用该组合，整体体验更好。Rider 也可以调试，但存在一些限制

Source Generator 由 csc 进程运行，因此若要在运行时调试生成器，需要即时附加调试器

> [!NOTE]
> 原作者在升级到 Rider 2020.3 后无法附加并调试生成器进程，不确定这是 Unity 2020.2 与 Rider 组合导致，还是 Rider 本身的问题。Unity 2020.1.2f 与 Rider 2020.1 的组合曾经能够正常附加和调试

项目提供了一些辅助工具，便于在可控时机附加到运行中的进程

可以使用 `SourceGeneratorHelper.cs` 提供的 `WaitForDebugger` 函数，使进程等待调试器附加。传入相应参数后，还可以只等待特定程序集或语法节点

仅在 Windows 上，可以使用 `System.Diagnostics.Debugger.Launch` 强制打开 Visual Studio 实例；该方法在 macOS 或 Linux 上无效。请注意，如果将它直接放入 `NetCodeSyntaxReceiver.VisitNode`，每个语法节点都会调用一次，可能产生数百个弹窗，因此应添加适当条件

生成器支持日志，并将日志追加到 `Temp/NetCodeGenerator.log`。可以随时记录 INFO、WARNING、ERROR 和 EXCEPTION。错误与异常也会显示在编辑器中，但不包含调用栈

<a id="faster-debugging-iteration-on-a-single-assembly"></a>
##### 针对单个程序集加快调试迭代

如果只想专注于某个程序集，通常可以按以下方式操作：

1. 从 `Temp` 文件夹找到与待调试程序集对应的 `TempUnityFile-XXX` 文件
2. 将该文件复制到临时目录，例如 `sgtests`，并改成更方便的名称，例如 `compile.out`
3. 打开终端并进入项目根目录
4. 使用类似以下命令手动执行编译

```text
Library/PackageCache/com.unity.rosyln@0.0.0-preview.8/Compiler~/mac/csc /noconfig @PATH-TO-YOUR-TEMP-FOLDER/compile.out
```

该命令会启动编译并调用生成器，之后按常规方式附加调试器即可
