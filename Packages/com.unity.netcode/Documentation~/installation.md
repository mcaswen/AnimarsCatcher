# Netcode for Entities 项目设置

设置 Netcode for Entities 前，需要确保正在使用正确的编辑器版本

## Unity 编辑器版本

Netcode for Entities 要求使用 Unity __2022.3.0f1__ 或更高版本

## IDE 支持

Entities 包使用 [Roslyn 源码生成器](https://docs.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/source-generators-overview)。为获得更好的编辑体验，建议使用兼容源码生成器的 IDE。以下 IDE 支持源码生成器：

* Visual Studio 2022+
* Rider 2021.3.3+

## 项目设置

1. 打开 __Unity Hub__ 并创建一个新的 __URP Project__
1. 打开 __Package Manager__（__Window__ > __Package Manager__）
1. 在 Package Manager 左上角的 __+__ 菜单中选择 __Add package by name__，添加以下包
    - com.unity.netcode
    - com.unity.entities.graphics

Package Manager 完成安装后，可以继续执行[后续步骤](networked-cube.md)
