---
name: run-unity-verification
description: "维护并使用 AnimarsCatcher 的独立 Unity 验证 worktree。用于主 Unity Editor 正占用主项目目录时执行脚本编译、Editor 验收、命令行 Play Mode、寻路 Benchmark 或 PlayMode Test，以及创建、更新、检查或移除验证 worktree。"
---

# 独立 Unity 验证

使用独立 Git worktree 运行第二个 Unity Editor，使它拥有自己的 `Library`、`Temp`、Asset Database 和脚本编译产物。不要让第二个完整 Editor 指向主项目目录。

## 固定位置

- 主项目：`D:\Unity-Projects\AnimarsCatcher`
- 验证 worktree：`D:\Unity-Projects\AnimarsCatcher-Verify`
- Unity 版本来源：`ProjectSettings/ProjectVersion.txt`
- 当前 Unity 安装：`D:\UnityEditors\6000.2.7f2\Editor\Unity.exe`
- Play Mode Runner：`AnimarsCatcher.Editor.LegacyNavigationBenchmarkBatchRunner`

始终从 `ProjectVersion.txt` 重新确认版本，不要只依赖本文记录的当前版本。

## 保护边界

1. 不要删除 `UnityLockfile`、`ArtifactDB-lock`、`SourceAssetDB-lock` 或其他占用文件。
2. 不要把主项目的 `Library`、`Temp`、`Logs`、`Obj` 或 `UserSettings` 复制到验证 worktree。
3. 只验证目标 Git 提交。主工作区的未提交修改不会出现在 worktree；发现未提交修改时明确报告这一点。
4. 更新 worktree 前确认没有 Unity 进程正在使用验证目录，并确认验证 worktree 没有未提交修改。
5. 验证 worktree 不干净时停止，不要自行使用 `reset --hard`、`clean`、覆盖文件或删除目录。
6. 不要在异步 Play Mode Runner 或 `-runTests` 命令中添加 `-quit`。Runner 和 Test Framework 负责结束进程。
7. 在受限执行环境中，创建、更新或移除工作区需要额外权限时，按实际 Git 命令申请授权。

## 创建 worktree

先检查注册状态和目标目录：

```powershell
$primaryRoot = "D:\Unity-Projects\AnimarsCatcher"
$verifyRoot = "D:\Unity-Projects\AnimarsCatcher-Verify"
$targetCommit = git -C $primaryRoot rev-parse HEAD

git -C $primaryRoot worktree list --porcelain
Test-Path -LiteralPath $verifyRoot
```

仅在 worktree 尚不存在时创建 detached worktree。使用 detached HEAD，避免同一个分支同时被两个 worktree 检出：

```powershell
git -C $primaryRoot worktree add --detach $verifyRoot $targetCommit
git -C $verifyRoot lfs pull
git -C $verifyRoot lfs fsck
```

创建后确认 `git worktree list --porcelain` 同时列出主目录和验证目录，且两者指向预期提交。

## 更新到待验证提交

先关闭使用验证目录的 Unity 实例，再执行：

```powershell
$primaryRoot = "D:\Unity-Projects\AnimarsCatcher"
$verifyRoot = "D:\Unity-Projects\AnimarsCatcher-Verify"
$targetCommit = git -C $primaryRoot rev-parse HEAD

git -C $primaryRoot status --short
git -C $verifyRoot status --short
```

验证目录必须没有输出。主目录可以有未提交修改，但这些修改不会被验证。确认目标提交后更新：

```powershell
git -C $verifyRoot switch --detach $targetCommit
git -C $verifyRoot lfs pull
git -C $verifyRoot lfs fsck
git -C $verifyRoot rev-parse HEAD
```

不要在 Unity 仍占用验证目录时切换提交。

## 定位 Unity Editor

从项目版本生成候选路径，并在执行前验证文件存在：

```powershell
$versionLine = Get-Content "$verifyRoot\ProjectSettings\ProjectVersion.txt" |
    Select-String '^m_EditorVersion:'
$unityVersion = $versionLine.Line -replace '^m_EditorVersion:\s*', ''
$unityCandidates = @(
    "D:\UnityEditors\$unityVersion\Editor\Unity.exe",
    "C:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
)
$unityExe = $unityCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

if ($null -eq $unityExe) {
    throw "没有找到 Unity $unityVersion，请先确认 Unity Hub 安装位置"
}
```

## 运行同步 Editor 验收

使用同步 `RunFromCommandLine` 方法验证脚本编译和阶段三算法。该方法返回后可以让 Unity 执行 `-quit`：

```powershell
$logDirectory = Join-Path $verifyRoot "Logs"
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$logPath = Join-Path $logDirectory "stage-three-validation.log"

& $unityExe `
    -batchmode `
    -nographics `
    -projectPath $verifyRoot `
    -executeMethod AnimarsCatcher.Navigation.Grid.Editor.NavigationGridStageThreeValidation.RunFromCommandLine `
    -logFile $logPath `
    -quit

if ($LASTEXITCODE -ne 0) {
    throw "Unity Editor 验收失败，退出码：$LASTEXITCODE；日志：$logPath"
}
```

Unity 会在调用 `-executeMethod` 前完成资源导入和脚本编译。首次运行需要创建完整的独立 `Library`，耗时会明显更长。

## 运行 Play Mode 寻路验证

只允许 `32`、`64` 或 `128` 个 Ani，并明确选择 `grid` 或 `legacy` 后端：

```powershell
$agentCount = 128
$backend = "grid"
$allowedCounts = @(32, 64, 128)
$allowedBackends = @("grid", "legacy")

if ($agentCount -notin $allowedCounts) {
    throw "Ani 数量必须是 32、64 或 128"
}
if ($backend -notin $allowedBackends) {
    throw "后端必须是 grid 或 legacy"
}

$entryPoint =
    "AnimarsCatcher.Editor.LegacyNavigationBenchmarkBatchRunner.Run${agentCount}FromCommandLine"
$verifiedCommit = git -C $verifyRoot rev-parse HEAD
$logDirectory = Join-Path $verifyRoot "Logs"
$logPath = Join-Path $logDirectory "navigation-$backend-$agentCount.log"
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

$unityArguments = @(
    "-batchmode",
    "-nographics",
    "-projectPath", $verifyRoot,
    "-benchmark-server-only",
    "-movement-backend=$backend",
    "-benchmark-git-commit=$verifiedCommit",
    "-executeMethod", $entryPoint,
    "-logFile", $logPath
)

& $unityExe @unityArguments
if ($LASTEXITCODE -ne 0) {
    throw "Unity Play Mode 验证失败，退出码：$LASTEXITCODE；日志：$logPath"
}
```

不要给这条命令添加 `-quit`。Runner 会打开统一 Benchmark 场景、进入 Play Mode、等待结果、退出 Play Mode，再用正确的退出码结束 Editor。

结果位置：

- Grid：`BenchmarkResults/GridNavigation`
- Legacy：`BenchmarkResults/LegacyNavigation`
- 日志：`Logs/navigation-<backend>-<count>.log`

## 运行 PlayMode Test

项目存在目标 PlayMode 测试程序集时使用 Test Framework：

```powershell
$testResultPath = Join-Path $verifyRoot "Logs\playmode-results.xml"
$testLogPath = Join-Path $verifyRoot "Logs\playmode-tests.log"

& $unityExe `
    -batchmode `
    -nographics `
    -projectPath $verifyRoot `
    -runTests `
    -testPlatform PlayMode `
    -testResults $testResultPath `
    -logFile $testLogPath

if ($LASTEXITCODE -ne 0) {
    throw "Unity PlayMode Test 失败，退出码：$LASTEXITCODE；日志：$testLogPath"
}
```

不要添加 `-quit`，否则测试可能在完成前被终止。

## 检查结果

每次运行后执行以下检查并报告目标提交、命令、退出码、日志和结果文件：

```powershell
git -C $verifyRoot rev-parse HEAD
Get-Content -Tail 100 -LiteralPath $logPath
git -C $verifyRoot status --short
```

只把 `BenchmarkResults` 和 `Logs` 当作本地验证产物。若 `git status` 显示受版本控制文件变化，先检查 Unity 是否改写了项目设置，再决定如何处理。

## 移除 worktree

只在用户明确要求移除时操作。先关闭验证 Unity，确认 worktree 干净，再使用 Git 移除：

```powershell
git -C $verifyRoot status --short
git -C $primaryRoot worktree remove $verifyRoot
git -C $primaryRoot worktree list --porcelain
```

不要直接用文件系统递归删除验证目录。
