param(
    [string]$RulesPath = (Join-Path $PSScriptRoot 'AssemblyMigrationRules.psd1'),
    [string]$JsonOutputPath = '',
    [switch]$FailOnWarnings
)

$ErrorActionPreference = 'Stop'

function ConvertTo-RepoPath
{
    param(
        [string]$FullPath,
        [string]$RepositoryRoot
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $normalizedPath = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $normalizedPath.StartsWith(
            $normalizedRoot,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Path is outside repository root: $normalizedPath"
    }

    $relativePath = $normalizedPath.Substring($normalizedRoot.Length)
    return $relativePath.TrimStart([char[]]'\/').Replace('\', '/')
}

function Resolve-RepositoryPath
{
    param(
        [string]$Path,
        [string]$RepositoryRoot
    )

    if ([System.IO.Path]::IsPathRooted($Path))
    {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath(
        (Join-Path $RepositoryRoot $Path))
}

function Get-MatchingRule
{
    param(
        [string]$RepoPath,
        [object[]]$Rules
    )

    return $Rules |
        Where-Object {
            $RepoPath -eq $_.Path -or
            $RepoPath.StartsWith(
                "$($_.Path)/",
                [System.StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object { $_.Path.Length } -Descending |
        Select-Object -First 1
}

function Get-NamespaceName
{
    param([string]$Text)

    $match = [regex]::Match(
        $Text,
        '(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)')
    if ($match.Success)
    {
        return $match.Groups[1].Value
    }

    return ''
}

function Test-NamespaceAllowed
{
    param(
        [string]$Namespace,
        [object[]]$Prefixes
    )

    foreach ($prefix in $Prefixes)
    {
        if ($Namespace -eq $prefix -or
            $Namespace.StartsWith(
                "$prefix.",
                [System.StringComparison]::Ordinal))
        {
            return $true
        }
    }

    return $false
}

function Get-Lifecycle
{
    param(
        [string]$RepoPath,
        [string]$Text,
        [string]$RuleLifecycle
    )

    $trimmedText = $Text.Trim()
    $editorWrapped =
        $trimmedText.StartsWith('#if UNITY_EDITOR') -and
        $trimmedText.EndsWith('#endif')
    $editorPath = $RepoPath -match '/Editor/'
    $usesEditorApi =
        $Text -match '(?m)^\s*using\s+UnityEditor' -or
        $Text -match '#if\s+UNITY_EDITOR'

    if ($RuleLifecycle -eq 'Benchmark')
    {
        return 'Benchmark'
    }

    if ($editorPath -or $editorWrapped)
    {
        return 'Editor'
    }

    if ($usesEditorApi)
    {
        return 'Mixed'
    }

    if ($Text -match '\bBaker\s*<' -or
        $Text -match '\bclass\s+[A-Za-z0-9_]*Authoring\b')
    {
        return 'Authoring'
    }

    if ($RuleLifecycle -eq 'Presentation')
    {
        return 'Presentation'
    }

    return 'Runtime'
}

function Remove-NonCodeText
{
    param([string]$Text)

    $withoutBlockComments = [regex]::Replace(
        $Text,
        '/\*.*?\*/',
        ' ',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $withoutLineComments = [regex]::Replace(
        $withoutBlockComments,
        '(?m)//.*$',
        ' ')
    return [regex]::Replace(
        $withoutLineComments,
        '@?"(?:""|\\.|[^"\\])*"',
        ' ')
}

function Get-DeclaredTypeNames
{
    param([string]$Text)

    $matches = [regex]::Matches(
        $Text,
        '\b(?:class|struct|interface|enum|record(?:\s+class|\s+struct)?)\s+([A-Za-z_][A-Za-z0-9_]*)')
    return @($matches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

function Add-StringToSet
{
    param(
        [hashtable]$Sets,
        [string]$Key,
        [string]$Value
    )

    if (-not $Sets.ContainsKey($Key))
    {
        $Sets[$Key] = New-Object 'System.Collections.Generic.HashSet[string]'
    }

    [void]$Sets[$Key].Add($Value)
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedRulesPath = Resolve-RepositoryPath $RulesPath $repositoryRoot
if (-not (Test-Path -LiteralPath $resolvedRulesPath))
{
    throw "Assembly migration rules not found: $resolvedRulesPath"
}

$configuration = Import-PowerShellDataFile -LiteralPath $resolvedRulesPath
$resolvedGlobalNamespaceBaselinePath = Resolve-RepositoryPath `
    $configuration.GlobalNamespaceBaseline `
    $repositoryRoot
if (-not (Test-Path -LiteralPath $resolvedGlobalNamespaceBaselinePath))
{
    throw "Global namespace baseline not found: $resolvedGlobalNamespaceBaselinePath"
}

$legacyGlobalNamespacePaths = New-Object 'System.Collections.Generic.HashSet[string]'
Get-Content -LiteralPath $resolvedGlobalNamespaceBaselinePath -Encoding UTF8 |
    Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not $_.TrimStart().StartsWith('#')
    } |
    ForEach-Object {
        [void]$legacyGlobalNamespacePaths.Add($_.Trim().Replace('\', '/'))
    }

$rules = @($configuration.Rules | ForEach-Object {
    [PSCustomObject]@{
        Path = $_.Path.Replace('\', '/').TrimEnd('/')
        Assembly = $_.Assembly
        AsmdefPath = [string]$_.AsmdefPath
        AsmrefPath = [string]$_.AsmrefPath
        RootNamespace = [string]$_.RootNamespace
        Owner = $_.Owner
        Status = $_.Status
        Lifecycle = $_.Lifecycle
        NamespacePrefixes = @($_.NamespacePrefixes)
        RequireNamespace = [bool]$_.RequireNamespace
        EnforceDependencyBoundary = [bool]$_.EnforceDependencyBoundary
        AllowedProjectDependencies = @($_.AllowedProjectDependencies)
    }
})

$sourceRoot = Join-Path $repositoryRoot $configuration.SourceRoot
$sourceFiles = @(
    Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs' |
        Sort-Object FullName
)

$records = @()
$criticalIssues = New-Object 'System.Collections.Generic.List[string]'
$warnings = New-Object 'System.Collections.Generic.List[string]'
$assemblyDefinitions = @()
$assemblyReferences = @()

$configuredAsmdefPaths = New-Object 'System.Collections.Generic.HashSet[string]'
$configuredAsmrefPaths = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($rule in $rules)
{
    if (-not [string]::IsNullOrWhiteSpace($rule.AsmdefPath))
    {
        [void]$configuredAsmdefPaths.Add($rule.AsmdefPath.Replace('\', '/'))
    }

    if (-not [string]::IsNullOrWhiteSpace($rule.AsmrefPath))
    {
        [void]$configuredAsmrefPaths.Add($rule.AsmrefPath.Replace('\', '/'))
    }
}

$discoveredAsmdefPaths = @(
    Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.asmdef' |
        ForEach-Object { ConvertTo-RepoPath $_.FullName $repositoryRoot }
)
$discoveredAsmrefPaths = @(
    Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.asmref' |
        ForEach-Object { ConvertTo-RepoPath $_.FullName $repositoryRoot }
)

foreach ($asmdefPath in $discoveredAsmdefPaths)
{
    if (-not $configuredAsmdefPaths.Contains($asmdefPath))
    {
        $criticalIssues.Add("Unregistered assembly definition: $asmdefPath")
    }
}

foreach ($asmrefPath in $discoveredAsmrefPaths)
{
    if (-not $configuredAsmrefPaths.Contains($asmrefPath))
    {
        $criticalIssues.Add("Unregistered assembly reference: $asmrefPath")
    }
}

foreach ($rule in $rules | Where-Object { -not [string]::IsNullOrWhiteSpace($_.AsmdefPath) })
{
    $resolvedAsmdefPath = Resolve-RepositoryPath $rule.AsmdefPath $repositoryRoot
    if (-not (Test-Path -LiteralPath $resolvedAsmdefPath))
    {
        $criticalIssues.Add("Assembly definition missing: $($rule.AsmdefPath)")
        continue
    }

    try
    {
        $definition = Get-Content -LiteralPath $resolvedAsmdefPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch
    {
        $criticalIssues.Add("Assembly definition is invalid JSON: $($rule.AsmdefPath)")
        continue
    }

    if ($definition.name -ne $rule.Assembly)
    {
        $criticalIssues.Add(
            "Assembly name mismatch: $($rule.AsmdefPath) -> $($definition.name)")
    }

    if ($definition.rootNamespace -ne $rule.RootNamespace)
    {
        $criticalIssues.Add(
            "Root namespace mismatch: $($rule.AsmdefPath) -> $($definition.rootNamespace)")
    }

    $expectedAutoReferenced = [bool]$configuration.ProjectAssembliesAutoReferenced
    if ([bool]$definition.autoReferenced -ne $expectedAutoReferenced)
    {
        $criticalIssues.Add(
            "Assembly Auto Referenced mismatch: $($rule.AsmdefPath) -> $($definition.autoReferenced)")
    }

    if ($definition.allowUnsafeCode)
    {
        $criticalIssues.Add("Assembly must not allow unsafe code: $($rule.AsmdefPath)")
    }

    if ($definition.overrideReferences)
    {
        $criticalIssues.Add("Assembly must not override references: $($rule.AsmdefPath)")
    }

    if ($definition.noEngineReferences)
    {
        $criticalIssues.Add("Assembly requires Unity engine references: $($rule.AsmdefPath)")
    }

    $nonGuidReferences = @(
        $definition.references |
            Where-Object { -not $_.StartsWith('GUID:', [System.StringComparison]::Ordinal) }
    )
    if ($nonGuidReferences.Count -gt 0)
    {
        $criticalIssues.Add("Assembly references must use GUID form: $($rule.AsmdefPath)")
    }

    $resolvedAsmdefMetaPath = "$resolvedAsmdefPath.meta"
    $assemblyGuid = ''
    if (-not (Test-Path -LiteralPath $resolvedAsmdefMetaPath))
    {
        $criticalIssues.Add("Assembly definition meta missing: $($rule.AsmdefPath).meta")
    }
    else
    {
        $metaText = Get-Content -LiteralPath $resolvedAsmdefMetaPath -Raw -Encoding UTF8
        $guidMatch = [regex]::Match($metaText, '(?m)^guid:\s*([0-9a-f]+)\s*$')
        if (-not $guidMatch.Success)
        {
            $criticalIssues.Add("Assembly definition GUID missing: $($rule.AsmdefPath).meta")
        }
        else
        {
            $assemblyGuid = $guidMatch.Groups[1].Value
        }
    }

    $assemblyDefinitions += [PSCustomObject]@{
        Path = $rule.AsmdefPath
        Assembly = $definition.name
        Guid = $assemblyGuid
        RootNamespace = $definition.rootNamespace
        ReferenceCount = @($definition.references).Count
        UsesGuidReferences = $nonGuidReferences.Count -eq 0
        AutoReferenced = [bool]$definition.autoReferenced
    }
}

foreach ($rule in $rules | Where-Object { -not [string]::IsNullOrWhiteSpace($_.AsmrefPath) })
{
    $resolvedAsmrefPath = Resolve-RepositoryPath $rule.AsmrefPath $repositoryRoot
    if (-not (Test-Path -LiteralPath $resolvedAsmrefPath))
    {
        $criticalIssues.Add("Assembly reference missing: $($rule.AsmrefPath)")
        continue
    }

    try
    {
        $referenceDefinition = Get-Content -LiteralPath $resolvedAsmrefPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch
    {
        $criticalIssues.Add("Assembly reference is invalid JSON: $($rule.AsmrefPath)")
        continue
    }

    $targetDefinition = @(
        $assemblyDefinitions |
            Where-Object Assembly -eq $rule.Assembly
    ) | Select-Object -First 1
    if ($null -eq $targetDefinition -or [string]::IsNullOrWhiteSpace($targetDefinition.Guid))
    {
        $criticalIssues.Add("Assembly reference target is unavailable: $($rule.AsmrefPath) -> $($rule.Assembly)")
        continue
    }

    $expectedReference = "GUID:$($targetDefinition.Guid)"
    if ($referenceDefinition.reference -ne $expectedReference)
    {
        $criticalIssues.Add(
            "Assembly reference target mismatch: $($rule.AsmrefPath) -> $($referenceDefinition.reference)")
    }

    $assemblyReferences += [PSCustomObject]@{
        Path = $rule.AsmrefPath
        Assembly = $rule.Assembly
        Reference = $referenceDefinition.reference
        ExpectedReference = $expectedReference
    }
}

foreach ($file in $sourceFiles)
{
    $repoPath = ConvertTo-RepoPath $file.FullName $repositoryRoot
    $rule = Get-MatchingRule $repoPath $rules
    if ($null -eq $rule)
    {
        $criticalIssues.Add("Unassigned script: $repoPath")
        continue
    }

    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $namespace = Get-NamespaceName $text
    $namespaceAllowed =
        [string]::IsNullOrEmpty($namespace) -or
        (Test-NamespaceAllowed $namespace $rule.NamespacePrefixes)

    if ($rule.RequireNamespace -and [string]::IsNullOrEmpty($namespace))
    {
        $criticalIssues.Add("Required namespace missing: $repoPath")
    }
    elseif ([string]::IsNullOrEmpty($namespace) -and
        -not $legacyGlobalNamespacePaths.Contains($repoPath))
    {
        $criticalIssues.Add("New global namespace script: $repoPath")
    }
    elseif (-not $namespaceAllowed)
    {
        $warnings.Add("Namespace outside planned prefix: $repoPath -> $namespace")
    }

    $lifecycle = Get-Lifecycle $repoPath $text $rule.Lifecycle
    if ($lifecycle -eq 'Editor' -and $repoPath -notmatch '/Editor/')
    {
        $warnings.Add("Editor-only script outside Editor directory: $repoPath")
    }
    elseif ($lifecycle -eq 'Mixed')
    {
        $warnings.Add("Mixed Runtime and Editor compilation: $repoPath")
    }

    $records += [PSCustomObject]@{
        Path = $repoPath
        Assembly = $rule.Assembly
        Owner = $rule.Owner
        Status = $rule.Status
        PlannedLifecycle = $rule.Lifecycle
        DetectedLifecycle = $lifecycle
        Namespace = $namespace
        HasNamespace = -not [string]::IsNullOrEmpty($namespace)
        Text = $text
        DeclaredTypes = @(Get-DeclaredTypeNames $text)
    }
}

$declarationsByName = @{}
foreach ($record in $records)
{
    foreach ($typeName in $record.DeclaredTypes)
    {
        if (-not $declarationsByName.ContainsKey($typeName))
        {
            $declarationsByName[$typeName] = @()
        }

        $declarationsByName[$typeName] += $record
    }
}

$uniqueDeclarations = @{}
foreach ($entry in $declarationsByName.GetEnumerator())
{
    $distinctFiles = @($entry.Value | Select-Object -ExpandProperty Path -Unique)
    if ($distinctFiles.Count -eq 1)
    {
        $uniqueDeclarations[$entry.Key] = $entry.Value[0]
    }
}

$edgeTypes = @{}
$edgeFiles = @{}
foreach ($record in $records)
{
    $codeText = Remove-NonCodeText $record.Text
    $identifiers = @(
        [regex]::Matches($codeText, '\b[A-Z][A-Za-z0-9_]*\b') |
            ForEach-Object { $_.Value } |
            Sort-Object -Unique
    )

    foreach ($identifier in $identifiers)
    {
        if (-not $uniqueDeclarations.ContainsKey($identifier))
        {
            continue
        }

        $targetRecord = $uniqueDeclarations[$identifier]
        if ($record.Assembly -eq $targetRecord.Assembly)
        {
            continue
        }

        $edgeKey = "$($record.Assembly)|$($targetRecord.Assembly)"
        Add-StringToSet $edgeTypes $edgeKey $identifier
        Add-StringToSet $edgeFiles $edgeKey $record.Path
    }
}

$dependencies = @()
foreach ($edgeKey in $edgeTypes.Keys | Sort-Object)
{
    $parts = $edgeKey.Split('|')
    $dependencies += [PSCustomObject]@{
        Source = $parts[0]
        Target = $parts[1]
        ReferencedTypes = @($edgeTypes[$edgeKey] | Sort-Object)
        SourceFiles = @($edgeFiles[$edgeKey] | Sort-Object)
    }
}

$mutualDependencies = @()
$seenPairs = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($dependency in $dependencies)
{
    $reverseKey = "$($dependency.Target)|$($dependency.Source)"
    if (-not $edgeTypes.ContainsKey($reverseKey))
    {
        continue
    }

    $orderedPair = @($dependency.Source, $dependency.Target) | Sort-Object
    $pairKey = $orderedPair -join '|'
    if ($seenPairs.Add($pairKey))
    {
        $mutualDependencies += [PSCustomObject]@{
            Left = $orderedPair[0]
            Right = $orderedPair[1]
        }
    }
}

$navigationExternalDependencies = @(
    $dependencies |
        Where-Object {
            $_.Source -eq 'AnimarsCatcher.Navigation' -and
            $_.Target -ne 'AnimarsCatcher.Navigation'
        }
)

$dependencyBoundaryViolations = @()
foreach ($rule in $rules | Where-Object EnforceDependencyBoundary)
{
    $violations = @(
        $dependencies |
            Where-Object {
                $_.Source -eq $rule.Assembly -and
                $_.Target -notin $rule.AllowedProjectDependencies
            }
    )

    foreach ($violation in $violations)
    {
        $dependencyBoundaryViolations += $violation
        $criticalIssues.Add(
            "$($rule.Assembly) has forbidden project dependency: $($violation.Target)")
    }
}

$currentGlobalNamespacePaths = New-Object 'System.Collections.Generic.HashSet[string]'
$records |
    Where-Object { -not $_.HasNamespace } |
    ForEach-Object { [void]$currentGlobalNamespacePaths.Add($_.Path) }
$staleGlobalNamespaceBaselinePaths = @(
    $legacyGlobalNamespacePaths |
        Where-Object { -not $currentGlobalNamespacePaths.Contains($_) } |
        Sort-Object
)
foreach ($stalePath in $staleGlobalNamespaceBaselinePaths)
{
    $warnings.Add("Stale global namespace baseline entry: $stalePath")
}

$assemblySummaries = @(
    $records |
        Group-Object Assembly |
        Sort-Object Name |
        ForEach-Object {
            $groupRecords = @($_.Group)
            [PSCustomObject]@{
                Assembly = $_.Name
                ScriptCount = $groupRecords.Count
                NamespacedCount = @($groupRecords | Where-Object HasNamespace).Count
                GlobalNamespaceCount = @($groupRecords | Where-Object { -not $_.HasNamespace }).Count
                Status = @($groupRecords | Select-Object -ExpandProperty Status -Unique) -join ', '
                Lifecycles = @(
                    $groupRecords |
                        Select-Object -ExpandProperty DetectedLifecycle -Unique |
                        Sort-Object
                )
            }
        }
)

$lifecycleSummaries = @(
    $records |
        Group-Object DetectedLifecycle |
        Sort-Object Name |
        ForEach-Object {
            [PSCustomObject]@{
                Lifecycle = $_.Name
                ScriptCount = $_.Count
            }
        }
)

$report = [PSCustomObject]@{
    SchemaVersion = 5
    GeneratedAt = (Get-Date).ToString('s')
    RepositoryRoot = $repositoryRoot
    RulesPath = ConvertTo-RepoPath $resolvedRulesPath $repositoryRoot
    GlobalNamespaceBaselinePath = ConvertTo-RepoPath `
        $resolvedGlobalNamespaceBaselinePath `
        $repositoryRoot
    Summary = [PSCustomObject]@{
        ScriptCount = $sourceFiles.Count
        AssignedCount = $records.Count
        NamespacedCount = @($records | Where-Object HasNamespace).Count
        GlobalNamespaceCount = @($records | Where-Object { -not $_.HasNamespace }).Count
        DependencyCount = $dependencies.Count
        MutualDependencyCount = $mutualDependencies.Count
        NavigationExternalDependencyCount = $navigationExternalDependencies.Count
        DependencyBoundaryViolationCount = $dependencyBoundaryViolations.Count
        StaleGlobalNamespaceBaselineCount = $staleGlobalNamespaceBaselinePaths.Count
        AssemblyDefinitionCount = $assemblyDefinitions.Count
        AssemblyReferenceCount = $assemblyReferences.Count
        WarningCount = $warnings.Count
        CriticalIssueCount = $criticalIssues.Count
    }
    Assemblies = $assemblySummaries
    AssemblyDefinitions = $assemblyDefinitions
    AssemblyReferences = $assemblyReferences
    Lifecycles = $lifecycleSummaries
    Dependencies = $dependencies
    MutualDependencies = $mutualDependencies
    DependencyBoundaryViolations = $dependencyBoundaryViolations
    Warnings = @($warnings)
    CriticalIssues = @($criticalIssues)
    Scripts = @(
        $records | ForEach-Object {
            [PSCustomObject]@{
                Path = $_.Path
                Assembly = $_.Assembly
                Owner = $_.Owner
                Status = $_.Status
                PlannedLifecycle = $_.PlannedLifecycle
                DetectedLifecycle = $_.DetectedLifecycle
                Namespace = $_.Namespace
            }
        }
    )
}

Write-Output 'Assembly migration audit'
Write-Output "Scripts: $($report.Summary.ScriptCount)"
Write-Output "Assigned: $($report.Summary.AssignedCount)"
Write-Output "Namespaced: $($report.Summary.NamespacedCount)"
Write-Output "Global namespace: $($report.Summary.GlobalNamespaceCount)"
Write-Output "Candidate dependencies: $($report.Summary.DependencyCount)"
Write-Output "Direct mutual dependencies: $($report.Summary.MutualDependencyCount)"
Write-Output "Navigation external dependencies: $($report.Summary.NavigationExternalDependencyCount)"
Write-Output "Dependency boundary violations: $($report.Summary.DependencyBoundaryViolationCount)"
Write-Output "Stale global namespace baseline: $($report.Summary.StaleGlobalNamespaceBaselineCount)"
Write-Output "Assembly definitions: $($report.Summary.AssemblyDefinitionCount)"
Write-Output "Assembly references: $($report.Summary.AssemblyReferenceCount)"
Write-Output "Warnings: $($report.Summary.WarningCount)"
Write-Output "Critical issues: $($report.Summary.CriticalIssueCount)"

Write-Output ''
Write-Output 'Candidate assemblies'
$assemblySummaries |
    Format-Table Assembly, ScriptCount, NamespacedCount, GlobalNamespaceCount, Status -AutoSize |
    Out-String |
    Write-Output

if ($mutualDependencies.Count -gt 0)
{
    Write-Output 'Direct mutual dependency candidates'
    $mutualDependencies |
        Format-Table Left, Right -AutoSize |
        Out-String |
        Write-Output
}

if ($warnings.Count -gt 0)
{
    Write-Output 'Warnings'
    $warnings | ForEach-Object { Write-Output "- $_" }
}

if (-not [string]::IsNullOrWhiteSpace($JsonOutputPath))
{
    $resolvedOutputPath = Resolve-RepositoryPath $JsonOutputPath $repositoryRoot
    $outputDirectory = Split-Path -Parent $resolvedOutputPath
    if (-not (Test-Path -LiteralPath $outputDirectory))
    {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }

    $report |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $resolvedOutputPath -Encoding UTF8
    Write-Output "JSON report: $(ConvertTo-RepoPath $resolvedOutputPath $repositoryRoot)"
}

if ($criticalIssues.Count -gt 0)
{
    $criticalIssues | ForEach-Object { Write-Error $_ }
    exit 1
}

if ($FailOnWarnings -and $warnings.Count -gt 0)
{
    exit 2
}

exit 0
