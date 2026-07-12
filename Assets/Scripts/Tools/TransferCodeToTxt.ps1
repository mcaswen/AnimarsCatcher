param(
    # 只统计/导出的目标文件夹（必填）
    [Parameter(Mandatory = $true)]
    [string]$InputFolder,

    # 输出 txt 文件路径
    [string]$OutputFile = "AllCSharpCode.txt",

    # 是否递归扫描子文件夹
    [switch]$Recurse = $true,

    # 是否排除常见无关目录
    [switch]$ExcludeCommonDirs = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"


function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)] [string]$BasePath,
        [Parameter(Mandatory = $true)] [string]$FullPath
    )

    # 兼容 Windows PowerShell 5.1
    $base = (Resolve-Path $BasePath).Path.TrimEnd('\') + '\'
    $full = (Resolve-Path $FullPath).Path

    $baseUri = New-Object System.Uri($base)
    $fullUri = New-Object System.Uri($full)

    $relUri = $baseUri.MakeRelativeUri($fullUri)
    $rel = [System.Uri]::UnescapeDataString($relUri.ToString())

    # 统一成 /
    return ($rel -replace '\\','/')
}


function Read-TextSmart {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    # 读取 bytes，判断 BOM / 编码
    [byte[]]$bytes = [System.IO.File]::ReadAllBytes($Path)

    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        # UTF-8 BOM
        return (New-Object System.Text.UTF8Encoding($true)).GetString($bytes)
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        # UTF-16 LE BOM
        return [System.Text.Encoding]::Unicode.GetString($bytes)
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        # UTF-16 BE BOM
        return [System.Text.Encoding]::BigEndianUnicode.GetString($bytes)
    }

    # 无 BOM：先尝试 UTF-8 严格解码（遇到非法字节就抛异常）
    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        return $utf8Strict.GetString($bytes)
    }
    catch {
        # UTF-8 不行：回退 GB18030
        $gb = [System.Text.Encoding]::GetEncoding("GB18030")
        return $gb.GetString($bytes)
    }
}


# 参数与路径整理
$inputRoot = (Resolve-Path $InputFolder).Path
$outputPath = (Resolve-Path (Split-Path -Parent $OutputFile) -ErrorAction SilentlyContinue)

if (-not $outputPath) {
    # OutputFile 可能是相对路径且目录不存在，创建之
    $outDir = Split-Path -Parent $OutputFile
    if ([string]::IsNullOrWhiteSpace($outDir)) {
        $outDir = "."
    }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$outputFullPath = (Resolve-Path (Split-Path -Parent $OutputFile)).Path + "\" + (Split-Path -Leaf $OutputFile)


# 文件收集
$excludeDirRegex = $null
if ($ExcludeCommonDirs) {
    # 排除目录
    $excludeDirRegex = "\\(\.git|\.svn|\.hg|\.vs|bin|obj|Library|Temp|Logs|UserSettings)\\"
}

$gciParams = @{
    Path   = $inputRoot
    Filter = "*.cs"
    File   = $true
}
if ($Recurse) { $gciParams.Recurse = $true }

$files = Get-ChildItem @gciParams | Where-Object {
    if (-not $ExcludeCommonDirs) { return $true }
    return ($_.FullName -notmatch $excludeDirRegex)
} | Sort-Object FullName


# 输出
$utf8Bom = New-Object System.Text.UTF8Encoding($true)

$stream = New-Object System.IO.StreamWriter($outputFullPath, $false, $utf8Bom)

try {
    $stream.NewLine = "`r`n"   # Windows 风格换行，观感更统一

    $stream.WriteLine("Input Folder: $inputRoot")
    $stream.WriteLine("File Count: $($files.Count)")
    $stream.WriteLine(("=" * 90))
    $stream.WriteLine("")

    $totalLines = 0

    foreach ($file in $files) {
        $relativePath = Get-RelativePath -BasePath $inputRoot -FullPath $file.FullName

        $text = Read-TextSmart -Path $file.FullName

        # 物理行数统计：用正则按 \r?\n 切分
        if ([string]::IsNullOrEmpty($text)) {
            $lineCount = 0
        } else {
            $lineCount = ([regex]::Split($text, "\r?\n")).Count
        }

        $totalLines += $lineCount

        $stream.WriteLine("// ===== FILE: $relativePath =====")
        $stream.WriteLine("// Physical Lines: $lineCount")
        $stream.WriteLine("")

        $stream.Write($text)
        if (-not $text.EndsWith("`n")) { $stream.WriteLine("") }

        $stream.WriteLine("")
        $stream.WriteLine(("-" * 90))
        $stream.WriteLine("")
    }

    $stream.WriteLine(("=" * 90))
    $stream.WriteLine("TOTAL Physical Lines: $totalLines")
}
finally {
    $stream.Dispose()
}

Write-Host "输出完成: $outputFullPath"
Write-Host "文件数: $($files.Count)"