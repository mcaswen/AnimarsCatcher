[CmdletBinding()]
param(
    [string]$SourcePath = "",
    [double]$MinimumRate = 15,
    [double]$RecommendedRate = 17,
    [switch]$NoFail
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path (Split-Path -Parent $PSScriptRoot) "Assets\Scripts"
}

$sourceRoot = (Resolve-Path $SourcePath).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$excludedDirectoryNames = @("Obsolete", "ThirdParty", "Samples", "Generated")
$violations = [System.Collections.Generic.List[object]]::new()
$moduleStats = @{}
$commentLines = 0
$codeLines = 0
$scannedFiles = 0

function Test-ExcludedPath {
    param([System.IO.FileInfo]$File)

    $relativePath = $File.FullName.Substring($sourceRoot.Length).TrimStart([char[]]"\/")
    $segments = $relativePath -split '[\\/]'
    foreach ($segment in $segments) {
        if ($excludedDirectoryNames -contains $segment) {
            return $true
        }
    }

    return $File.Name -match '(?i)(\.g\.cs$|\.generated\.cs$|\.designer\.cs$)'
}

function Get-NormalizedCommentText {
    param([string]$Text)

    $normalized = $Text.Trim()
    $normalized = $normalized -replace '^[/*\s]+', ''
    $normalized = $normalized -replace '[*/\s]+$', ''
    return $normalized.Trim()
}

function Add-Violation {
    param(
        [string]$Path,
        [int]$Line,
        [string]$Rule,
        [string]$Text
    )

    $violations.Add([pscustomobject]@{
        Path = $Path
        Line = $Line
        Rule = $Rule
        Text = $Text.Trim()
    })
}

function Test-CommentedCode {
    param([string]$Text)

    $codePatterns = @(
        '^\s*(using|namespace)\s+[A-Za-z_]',
        '^\s*(public|private|protected|internal)\s+',
        '^\s*(class|struct|interface|enum|record)\s+',
        '^\s*(if|for|foreach|while|switch|catch|lock)\s*\(',
        '^\s*(return|throw|yield|await)\b',
        '^\s*(Debug|Console)\.[A-Za-z_]+\s*\(',
        '^\s*#(if|elif|else|endif|region|endregion)\b',
        '^\s*[{}]\s*$',
        '^\s*\[[A-Za-z_][^]]*\]\s*$',
        '^\s*[A-Za-z_][\w<>,.?\[\]\s]*\s+[A-Za-z_]\w*\s*(=|;|\()'
    )

    foreach ($pattern in $codePatterns) {
        if ($Text -match $pattern) {
            return $true
        }
    }

    return $false
}

function Get-CSharpStructureLine {
    param(
        [string]$Line,
        [ref]$InBlockComment,
        [ref]$InVerbatimString
    )

    $builder = [System.Text.StringBuilder]::new()
    $index = 0

    while ($index -lt $Line.Length) {
        if ($InBlockComment.Value) {
            $endIndex = $Line.IndexOf('*/', $index, [System.StringComparison]::Ordinal)
            if ($endIndex -lt 0) {
                break
            }

            $InBlockComment.Value = $false
            $index = $endIndex + 2
            continue
        }

        if ($InVerbatimString.Value) {
            $quoteIndex = $Line.IndexOf([char]34, $index)
            if ($quoteIndex -lt 0) {
                break
            }

            if ($quoteIndex + 1 -lt $Line.Length -and $Line[$quoteIndex + 1] -eq [char]34) {
                $index = $quoteIndex + 2
                continue
            }

            $InVerbatimString.Value = $false
            $index = $quoteIndex + 1
            continue
        }

        if ($index + 1 -lt $Line.Length -and $Line[$index] -eq '/' -and $Line[$index + 1] -eq '/') {
            break
        }

        if ($index + 1 -lt $Line.Length -and $Line[$index] -eq '/' -and $Line[$index + 1] -eq '*') {
            $InBlockComment.Value = $true
            $index += 2
            continue
        }

        $startsVerbatimString =
            ($index + 1 -lt $Line.Length -and $Line[$index] -eq '@' -and $Line[$index + 1] -eq [char]34) -or
            ($index + 2 -lt $Line.Length -and $Line[$index] -eq '$' -and $Line[$index + 1] -eq '@' -and $Line[$index + 2] -eq [char]34) -or
            ($index + 2 -lt $Line.Length -and $Line[$index] -eq '@' -and $Line[$index + 1] -eq '$' -and $Line[$index + 2] -eq [char]34)

        if ($startsVerbatimString) {
            $InVerbatimString.Value = $true
            $index += if ($Line[$index] -eq '@' -and $Line[$index + 1] -eq [char]34) { 2 } else { 3 }
            continue
        }

        if ($Line[$index] -eq [char]34 -or $Line[$index] -eq [char]39) {
            $delimiter = $Line[$index]
            $index++
            while ($index -lt $Line.Length) {
                if ($Line[$index] -eq [char]92) {
                    $index += 2
                    continue
                }
                if ($Line[$index] -eq $delimiter) {
                    $index++
                    break
                }
                $index++
            }
            continue
        }

        $null = $builder.Append($Line[$index])
        $index++
    }

    return $builder.ToString()
}

function Get-XmlOwnerDeclaration {
    param(
        [string[]]$Lines,
        [int]$SummaryLineIndex
    )

    $attributeDepth = 0

    for ($lineIndex = $SummaryLineIndex + 1; $lineIndex -lt $Lines.Count; $lineIndex++) {
        $trimmedLine = $Lines[$lineIndex].Trim()
        if ([string]::IsNullOrWhiteSpace($trimmedLine) -or $trimmedLine.StartsWith('///') -or $trimmedLine.StartsWith('#')) {
            continue
        }

        if ($attributeDepth -gt 0 -or $trimmedLine.StartsWith('[')) {
            $attributeDepth += ([regex]::Matches($trimmedLine, '\[')).Count
            $attributeDepth -= ([regex]::Matches($trimmedLine, '\]')).Count
            if ($attributeDepth -le 0) {
                $attributeDepth = 0
                $closingBracketIndex = $trimmedLine.LastIndexOf(']')
                if ($closingBracketIndex -ge 0 -and $closingBracketIndex + 1 -lt $trimmedLine.Length) {
                    $remainingText = $trimmedLine.Substring($closingBracketIndex + 1).Trim()
                    if (-not [string]::IsNullOrWhiteSpace($remainingText)) {
                        return $remainingText
                    }
                }
            }
            continue
        }

        if ($trimmedLine.StartsWith('//') -or $trimmedLine.StartsWith('/*')) {
            return ''
        }

        return $trimmedLine
    }

    return ''
}

function Get-ImplementationXmlSummaryLines {
    param([string[]]$Lines)

    $implementationSummaryLines = @{}
    $typeStack = [System.Collections.Generic.List[object]]::new()
    $braceDepth = 0
    $pendingType = $null
    $inBlockComment = $false
    $inVerbatimString = $false
    $modifierPattern = '(?:(?:public|private|protected|internal|file|static|abstract|sealed|partial|readonly|ref|unsafe|new|virtual|override|extern|async)\s+)*'
    $typePattern = '^(?<modifiers>' + $modifierPattern + ')(?<kind>record\s+(?:class|struct)|record|class|struct|interface|enum)\b'

    for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
        while ($typeStack.Count -gt 0 -and $braceDepth -lt $typeStack[$typeStack.Count - 1].BodyDepth) {
            $typeStack.RemoveAt($typeStack.Count - 1)
        }

        $currentType = if ($typeStack.Count -gt 0) { $typeStack[$typeStack.Count - 1] } else { $null }
        $rawLine = $Lines[$lineIndex]

        if ($rawLine -match '^\s*///\s*<summary>\s*$') {
            $declaration = Get-XmlOwnerDeclaration -Lines $Lines -SummaryLineIndex $lineIndex
            if (-not [string]::IsNullOrWhiteSpace($declaration)) {
                $isDirectTypeMember = $null -ne $currentType -and $braceDepth -eq $currentType.BodyDepth
                $isTypeDeclaration = $declaration -match $typePattern
                $modifiers = if ($declaration -match '^(' + $modifierPattern + ')') { $Matches[1] } else { '' }
                $hasPrivateModifier = $modifiers -match '\bprivate\b'
                $hasApiModifier = $modifiers -match '\b(public|protected|internal)\b'
                $isImplementation = $false

                if ($isTypeDeclaration) {
                    if ($null -ne $currentType) {
                        if ($currentType.PrivateContext -or $hasPrivateModifier) {
                            $isImplementation = $true
                        }
                        elseif ($currentType.Kind -ne 'interface' -and (-not $hasApiModifier)) {
                            $isImplementation = $true
                        }
                    }
                }
                elseif ($isDirectTypeMember -and $currentType.Kind -ne 'enum') {
                    if ($currentType.PrivateContext -or $hasPrivateModifier) {
                        $isImplementation = $true
                    }
                    elseif ($currentType.Kind -ne 'interface' -and (-not $hasApiModifier)) {
                        $isImplementation = $true
                    }
                }

                if ($isImplementation) {
                    $implementationSummaryLines[$lineIndex] = $true
                }
            }
        }

        $structureLine = Get-CSharpStructureLine `
            -Line $rawLine `
            -InBlockComment ([ref]$inBlockComment) `
            -InVerbatimString ([ref]$inVerbatimString)
        $trimmedStructureLine = $structureLine.Trim()

        if ($null -ne $pendingType -and $trimmedStructureLine.Contains('{')) {
            $pendingType.BodyDepth = $braceDepth + 1
            $null = $typeStack.Add($pendingType)
            $pendingType = $null
        }

        if ($trimmedStructureLine -match $typePattern) {
            $typeModifiers = $Matches['modifiers']
            $typeKind = $Matches['kind']
            if ($typeKind.StartsWith('record')) {
                $typeKind = if ($typeKind.EndsWith('struct')) { 'struct' } else { 'class' }
            }

            $parentType = if ($typeStack.Count -gt 0) { $typeStack[$typeStack.Count - 1] } else { $null }
            $isNestedType = $null -ne $parentType -and $braceDepth -eq $parentType.BodyDepth
            $hasPrivateModifier = $typeModifiers -match '\bprivate\b'
            $hasApiModifier = $typeModifiers -match '\b(public|protected|internal)\b'
            $privateContext =
                ($null -ne $parentType -and $parentType.PrivateContext) -or
                ($isNestedType -and $hasPrivateModifier) -or
                ($isNestedType -and $parentType.Kind -ne 'interface' -and (-not $hasApiModifier))
            $typeInfo = [pscustomobject]@{
                Kind = $typeKind
                BodyDepth = -1
                PrivateContext = $privateContext
            }

            if ($trimmedStructureLine.Contains('{')) {
                $typeInfo.BodyDepth = $braceDepth + 1
                $null = $typeStack.Add($typeInfo)
            }
            elseif (-not $trimmedStructureLine.EndsWith(';')) {
                $pendingType = $typeInfo
            }
        }

        $braceDepth += ([regex]::Matches($structureLine, '\{')).Count
        $braceDepth -= ([regex]::Matches($structureLine, '\}')).Count
    }

    return $implementationSummaryLines
}

function Test-HasXmlSummaryBeforeLine {
    param(
        [string[]]$Lines,
        [int]$DeclarationLineIndex
    )

    $minimumLineIndex = [math]::Max(0, $DeclarationLineIndex - 40)
    for ($lineIndex = $DeclarationLineIndex - 1; $lineIndex -ge $minimumLineIndex; $lineIndex--) {
        $trimmedLine = $Lines[$lineIndex].Trim()
        if ([string]::IsNullOrWhiteSpace($trimmedLine)) {
            continue
        }

        if ($trimmedLine -match '^///\s*</summary>\s*$') {
            return $true
        }

        if ($trimmedLine.StartsWith('///') -or
            $trimmedLine.StartsWith('[') -or
            $trimmedLine.StartsWith('#')) {
            continue
        }

        if ($trimmedLine -eq '}' -or
            $trimmedLine.StartsWith('namespace ') -or
            $trimmedLine.EndsWith(';')) {
            return $false
        }
    }

    return $false
}

function Get-TopLevelPublicTypeDeclarations {
    param([string[]]$Lines)

    $declarations = [System.Collections.Generic.List[object]]::new()
    $typeStack = [System.Collections.Generic.List[object]]::new()
    $braceDepth = 0
    $pendingType = $null
    $inBlockComment = $false
    $inVerbatimString = $false
    $modifierPattern = '(?:(?:public|private|protected|internal|file|static|abstract|sealed|partial|readonly|ref|unsafe|new)\s+)*'
    $typePattern = '^(?<modifiers>' + $modifierPattern + ')(?<kind>record\s+(?:class|struct)|record|class|struct|interface|enum)\s+(?<name>[A-Za-z_]\w*)\b'

    for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
        while ($typeStack.Count -gt 0 -and $braceDepth -lt $typeStack[$typeStack.Count - 1].BodyDepth) {
            $typeStack.RemoveAt($typeStack.Count - 1)
        }

        $structureLine = Get-CSharpStructureLine `
            -Line $Lines[$lineIndex] `
            -InBlockComment ([ref]$inBlockComment) `
            -InVerbatimString ([ref]$inVerbatimString)
        $trimmedStructureLine = $structureLine.Trim()

        if ($null -ne $pendingType -and $trimmedStructureLine.Contains('{')) {
            $pendingType.BodyDepth = $braceDepth + 1
            $null = $typeStack.Add($pendingType)
            $pendingType = $null
        }

        if ($trimmedStructureLine -match $typePattern) {
            $modifiers = $Matches['modifiers']
            $typeName = $Matches['name']
            $isTopLevel = $typeStack.Count -eq 0
            $isPublic = $modifiers -match '\bpublic\b'
            if ($isTopLevel -and $isPublic) {
                $declarations.Add([pscustomobject]@{
                    Line = $lineIndex + 1
                    Name = $typeName
                    IsPartial = $modifiers -match '\bpartial\b'
                    HasSummary = Test-HasXmlSummaryBeforeLine `
                        -Lines $Lines `
                        -DeclarationLineIndex $lineIndex
                })
            }

            $typeInfo = [pscustomobject]@{ BodyDepth = -1 }
            if ($trimmedStructureLine.Contains('{')) {
                $typeInfo.BodyDepth = $braceDepth + 1
                $null = $typeStack.Add($typeInfo)
            }
            elseif (-not $trimmedStructureLine.EndsWith(';')) {
                $pendingType = $typeInfo
            }
        }

        $braceDepth += ([regex]::Matches($structureLine, '\{')).Count
        $braceDepth -= ([regex]::Matches($structureLine, '\}')).Count
    }

    return $declarations
}

function Read-CSharpFile {
    param([System.IO.FileInfo]$File)

    $relativePath = $File.FullName.Substring($projectRoot.Length).TrimStart([char[]]"\/").Replace('\', '/')
    $moduleRelativePath = $File.FullName.Substring($sourceRoot.Length).TrimStart([char[]]"\/")
    $moduleName = ($moduleRelativePath -split '[\\/]')[0]
    if (-not $moduleStats.ContainsKey($moduleName)) {
        $moduleStats[$moduleName] = [pscustomobject]@{ Comments = 0; Code = 0 }
    }

    $content = Get-Content -LiteralPath $File.FullName -Raw -Encoding UTF8
    $header = (($content -split "`r?`n") | Select-Object -First 10) -join "`n"
    if ($header -match '(?i)<auto-generated|auto generated code|generated by') {
        return
    }

    $script:scannedFiles++
    $inBlockComment = $false
    $inVerbatimString = $false
    $lines = $content -split "`r?`n"
    $implementationSummaryLines = Get-ImplementationXmlSummaryLines -Lines $lines
    $publicTypeDeclarations = Get-TopLevelPublicTypeDeclarations -Lines $lines
    foreach ($declaration in $publicTypeDeclarations) {
        $documentedByAnotherPartial =
            $declaration.IsPartial -and
            $script:documentedPublicPartialTypeNames.ContainsKey($declaration.Name)
        if (-not $declaration.HasSummary -and -not $documentedByAnotherPartial) {
            Add-Violation `
                $relativePath `
                $declaration.Line `
                "public-type-documentation" `
                "top-level public type must have an XML summary"
        }
    }

    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $line = $lines[$lineIndex]
        $segments = [System.Collections.Generic.List[string]]::new()
        $hasCode = $false
        $index = 0

        if ($line -match '\[(?:UnityEngine\.)?Tooltip\s*\(\s*"(?<tooltip>(?:\\.|[^"])*)"\s*\)\]') {
            $tooltipText = $Matches['tooltip']
            if ($tooltipText -notmatch '[\u3400-\u9FFF]') {
                Add-Violation $relativePath ($lineIndex + 1) "tooltip-language" "Tooltip must add a short Chinese explanation or be removed"
            }

            if ($tooltipText -match '(\u3002|\.)\s*$') {
                Add-Violation $relativePath ($lineIndex + 1) "tooltip-period" $tooltipText
            }
        }

        while ($index -lt $line.Length) {
            if ($inBlockComment) {
                $endIndex = $line.IndexOf('*/', $index, [System.StringComparison]::Ordinal)
                if ($endIndex -lt 0) {
                    $segments.Add($line.Substring($index))
                    $index = $line.Length
                    continue
                }

                $segments.Add($line.Substring($index, $endIndex - $index))
                $inBlockComment = $false
                $index = $endIndex + 2
                continue
            }

            if ($inVerbatimString) {
                $quoteIndex = $line.IndexOf([char]34, $index)
                if ($quoteIndex -lt 0) {
                    $index = $line.Length
                    continue
                }

                if ($quoteIndex + 1 -lt $line.Length -and $line[$quoteIndex + 1] -eq [char]34) {
                    $index = $quoteIndex + 2
                    continue
                }

                $inVerbatimString = $false
                $index = $quoteIndex + 1
                continue
            }

            if ([char]::IsWhiteSpace($line[$index])) {
                $index++
                continue
            }

            if ($index + 1 -lt $line.Length -and $line[$index] -eq '/' -and $line[$index + 1] -eq '/') {
                $segments.Add($line.Substring($index + 2))
                break
            }

            if ($index + 1 -lt $line.Length -and $line[$index] -eq '/' -and $line[$index + 1] -eq '*') {
                $inBlockComment = $true
                $index += 2
                continue
            }

            $hasCode = $true

            if ($index + 1 -lt $line.Length -and $line[$index] -eq '@' -and $line[$index + 1] -eq [char]34) {
                $inVerbatimString = $true
                $index += 2
                continue
            }

            if ($line[$index] -eq [char]34 -or $line[$index] -eq [char]39) {
                $delimiter = $line[$index]
                $index++
                while ($index -lt $line.Length) {
                    if ($line[$index] -eq [char]92) {
                        $index += 2
                        continue
                    }
                    if ($line[$index] -eq $delimiter) {
                        $index++
                        break
                    }
                    $index++
                }
                continue
            }

            $index++
        }

        if ($hasCode) {
            $script:codeLines++
            $moduleStats[$moduleName].Code++
        }

        if ($segments.Count -eq 0) {
            continue
        }

        if ($line.TrimStart().StartsWith('///')) {
            if ($line -match '</?summary>' -and $line -notmatch '^\s*///\s*</?summary>\s*$') {
                Add-Violation $relativePath ($lineIndex + 1) "xml-summary-layout" "summary tags must be on separate lines"
            }

            if ($line -match '^\s*///\s*<summary>\s*$') {
                $declaration = Get-XmlOwnerDeclaration -Lines $lines -SummaryLineIndex $lineIndex
                $templateCallbackPattern =
                    '^(?:(?:public|private|protected|internal|static|virtual|override|sealed|new|async)\s+)*' +
                    '(?:void|bool)\s+' +
                    '(?:Bake|OnCreate|OnUpdate|OnDestroy|Awake|Start|Update|LateUpdate|FixedUpdate|' +
                    'OnEnable|OnDisable|OnInspectorGUI|OnPreprocessBuild)\s*\('
                if ($declaration -match $templateCallbackPattern) {
                    Add-Violation `
                        $relativePath `
                        ($lineIndex + 1) `
                        "xml-template-callback" `
                        "framework callbacks must explain complex constraints with inline // comments"
                }
            }

            if ($implementationSummaryLines.ContainsKey($lineIndex)) {
                Add-Violation $relativePath ($lineIndex + 1) "xml-implementation-summary" "private implementation must use // comments"
            }

            $previousLineIndex = $lineIndex - 1
            while ($previousLineIndex -ge 0 -and [string]::IsNullOrWhiteSpace($lines[$previousLineIndex])) {
                $previousLineIndex--
            }

            if ($previousLineIndex -ge 0 -and $lines[$previousLineIndex].TrimStart().StartsWith('[')) {
                Add-Violation $relativePath ($lineIndex + 1) "xml-placement" "XML documentation must appear before attributes"
            }
        }

        $normalizedText = Get-NormalizedCommentText ($segments -join ' ')
        if ([string]::IsNullOrWhiteSpace($normalizedText)) {
            continue
        }

        $script:commentLines++
        $moduleStats[$moduleName].Comments++

        $prose = ($normalizedText -replace '<[^>]+>', ' ').Trim()
        if ([string]::IsNullOrWhiteSpace($prose)) {
            continue
        }

        if (Test-CommentedCode $prose) {
            Add-Violation $relativePath ($lineIndex + 1) "commented-code" $normalizedText
        }

        if ($prose -notmatch '[\u3400-\u9FFF]') {
            Add-Violation $relativePath ($lineIndex + 1) "language" $normalizedText
        }

        if ($prose -match '(\u3002|\.)\s*$') {
            Add-Violation $relativePath ($lineIndex + 1) "period" $normalizedText
        }
    }
}

$files = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs' |
    Where-Object { -not (Test-ExcludedPath $_) } |
    Sort-Object FullName

$documentedPublicPartialTypeNames = @{}
foreach ($file in $files) {
    $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $header = (($content -split "`r?`n") | Select-Object -First 10) -join "`n"
    if ($header -match '(?i)<auto-generated|auto generated code|generated by') {
        continue
    }

    $lines = $content -split "`r?`n"
    foreach ($declaration in (Get-TopLevelPublicTypeDeclarations -Lines $lines)) {
        if ($declaration.IsPartial -and $declaration.HasSummary) {
            $documentedPublicPartialTypeNames[$declaration.Name] = $true
        }
    }
}

foreach ($file in $files) {
    Read-CSharpFile $file
}

$denominator = $commentLines + $codeLines
$commentRate = if ($denominator -eq 0) { 0 } else { [math]::Round($commentLines * 100.0 / $denominator, 2) }

Write-Host "Comment style check"
Write-Host "Files: $scannedFiles"
Write-Host "Comment lines: $commentLines"
Write-Host "Code lines: $codeLines"
Write-Host "Comment rate: $commentRate% (minimum $MinimumRate%, recommended $RecommendedRate%)"
Write-Host ""
Write-Host "Module rates"

foreach ($entry in $moduleStats.GetEnumerator() | Sort-Object Name) {
    $moduleDenominator = $entry.Value.Comments + $entry.Value.Code
    $moduleRate = if ($moduleDenominator -eq 0) { 0 } else { [math]::Round($entry.Value.Comments * 100.0 / $moduleDenominator, 2) }
    Write-Host ("{0,-20} {1,6}%  comments {2,5}  code {3,5}" -f $entry.Name, $moduleRate, $entry.Value.Comments, $entry.Value.Code)
}

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "Violations: $($violations.Count)"
    foreach ($violation in $violations) {
        Write-Host ("[{0}] {1}:{2} {3}" -f $violation.Rule, $violation.Path, $violation.Line, $violation.Text)
    }
}

$failed = $commentRate -lt $MinimumRate -or $violations.Count -gt 0
if ($failed -and -not $NoFail) {
    exit 1
}

exit 0
