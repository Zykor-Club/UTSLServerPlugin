#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [string]$BuildType = "Release",
    [switch]$NoZip,
    [string]$UnifierTSLRepo = ""
)

Set-Location $PSScriptRoot/..
$ErrorActionPreference = "Stop"

$solutionPath = "./Plugin.slnx"

# Step 1: Build
if (-not $NoBuild) {
    Remove-Item "./out/$BuildType" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Building plugins..."
    $repoArgs = @()
    if ($UnifierTSLRepo) { $repoArgs += "-p:UnifierTSLRepo=$UnifierTSLRepo" }
    dotnet restore $solutionPath @repoArgs
    dotnet build $solutionPath -c $BuildType --no-restore @repoArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Step 2: Prepare publish directory
Remove-Item ./publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Name ./publish -ItemType Directory -Force | Out-Null

# ===========================================================================
# 模块族（模块本体 / 模块族玩法 / 契约库）目录布局支持
#
# 角色判定（扫描项目源码里的特性文本，允许命名空间前缀，例如
#            [assembly: UnifierTSL.Module.CoreModule]）：
#   [assembly: CoreModule]                  -> 模块本体；模块名 = 项目的 AssemblyName
#   [assembly: RequiresCoreModule("X")]     -> 模块族玩法；必须与模块 X 放在同一目录
#   [PluginMetadata(...)]（类级特性）        -> 普通插件；平铺在 publish/ 根目录
#   以上都没有                              -> 库（不是插件）；放到 publish/<模块名>/lib/
# 说明：CoreModule / RequiresCoreModule 是程序集级特性，PluginMetadata 目前是类级特性，
#       所以两种写法（带 assembly: 前缀或不带）都识别。
#
# 产出布局：
#   publish/<模块名>/<模块本体>.dll
#   publish/<模块名>/<族内玩法>.dll
#   publish/<模块名>/lib/<契约库>.dll
#   publish/<普通插件>.dll
#
# Plugins.json 只列出真正带 [PluginMetadata] 的程序集，库绝不出现；
# 字段含义保持原样，仅新增可选字段 Module（普通插件为 ""），Path 改为包内相对路径。
#
# 全文件保持 Windows PowerShell 5.1 兼容（不用 ?? / -AsHashtable / utf8NoBOM）。
# ===========================================================================
# 注意：参数部分刻意不用嵌套量词（[^()"]+ 再套 *），否则在缺少右括号时会产生
#       灾难性回溯把 PowerShell 卡死；这里每个分支只消耗单个字符/一个完整字符串。
$attributeRegex = [regex]'\[\s*(?:assembly\s*:\s*)?(?<name>[A-Za-z_][A-Za-z0-9_.]*)\s*(?<args>\((?:[^()"]|"(?:[^"\\]|\\.)*"|\([^()]*\))*\))?\s*\]'

# 去掉 .cs 源码里的注释：注释中出现的 [assembly: ...] 文本不参与判定
function Remove-CsComment {
    param([string]$Text)
    $t = [regex]::Replace($Text, '(?s)/\*.*?\*/', ' ')
    $t = [regex]::Replace($t, '(?m)//[^\r\n]*', ' ')
    return $t
}

# 特性名去掉可能的 Attribute 后缀
function Get-AttributeSimpleName {
    param([string]$Name)
    if ($Name.EndsWith("Attribute", [System.StringComparison]::Ordinal)) {
        return $Name.Substring(0, $Name.Length - 9)
    }
    return $Name
}

# 简单名比较，允许命名空间前缀：CoreModule / UnifierTSL.Module.CoreModule
function Test-AttributeName {
    param([string]$Name, [string]$Simple)
    if ($Name -eq $Simple) { return $true }
    return $Name.EndsWith(".$Simple", [System.StringComparison]::Ordinal)
}

# 归一化路径用于比较（CI 的 Linux runner 上 csproj 里的 ProjectReference 写的是反斜杠）
function Get-ComparablePath {
    param([string]$Path)
    $p = $Path
    try { $p = [System.IO.Path]::GetFullPath($Path) } catch { }
    return $p.Replace('\', '/').ToLowerInvariant()
}

# 按名字前缀兜底归属模块：VirtualWorldCore.Abstractions -> VirtualWorldCore
function Get-ModuleNameByPrefix {
    param([string]$AssemblyName, [hashtable]$ModuleNames)
    $first = $AssemblyName.Split('.')[0]
    if ($ModuleNames.ContainsKey($first)) { return $first }
    foreach ($m in @($ModuleNames.Keys | Sort-Object)) {
        if ($AssemblyName.StartsWith("$m.", [System.StringComparison]::Ordinal)) { return $m }
    }
    return ''
}

# 收集项目目录下参与编译的 .cs 源码
#   跳过 obj/ 与 bin/（构建产物），跳过内嵌的独立子工程目录（如 MiniMapSync/client、RuYouMusic/client）
function Get-ProjectSourceFile {
    param([string]$ProjectDir)
    $root = (Get-Item -LiteralPath $ProjectDir).FullName.TrimEnd('\', '/')
    $sep = [System.IO.Path]::DirectorySeparatorChar
    $skipDirs = @()
    foreach ($d in @(Get-ChildItem -LiteralPath $root -Directory -Recurse -ErrorAction SilentlyContinue)) {
        if ($d.FullName -eq $root) { continue }
        if ($d.Name -eq 'obj' -or $d.Name -eq 'bin') { $skipDirs += $d.FullName; continue }
        if (@(Get-ChildItem -LiteralPath $d.FullName -Filter *.csproj -File -ErrorAction SilentlyContinue).Count -gt 0) {
            $skipDirs += $d.FullName
        }
    }
    $files = @(Get-ChildItem -LiteralPath $root -Filter *.cs -File -Recurse -ErrorAction SilentlyContinue | Where-Object {
        $f = $_.FullName
        $skip = $false
        foreach ($sd in $skipDirs) {
            if ($f.StartsWith($sd + $sep, [System.StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
        }
        -not $skip
    } | Sort-Object -Property FullName)
    return $files
}

# Step 3: 收集项目信息并判定角色
Write-Host "Scanning project roles..."
$solution = [xml](Get-Content $solutionPath -Raw)
$allProjects = @($solution.Solution.Project)
$projects = @($allProjects | Where-Object { $_.Path -like "src/*/*.csproj" -and $_.Path -notlike "src/Shared/*" })

$projectInfos = @()

foreach ($proj in $projects) {
    $projPath = [string]$proj.Path
    $projDir = [System.IO.Path]::GetDirectoryName($projPath)
    $projFileName = [System.IO.Path]::GetFileNameWithoutExtension($projPath)
    $asmName = $projFileName

    try {
        $csprojContent = [xml](Get-Content $projPath -Raw)
    } catch {
        Write-Warning "Cannot parse $projPath, skipping..."
        continue
    }

    $pgList = @($csprojContent.Project.PropertyGroup)
    $asmProp = $pgList | Where-Object { $_.AssemblyName } | Select-Object -First 1
    if ($asmProp) { $asmName = [string]$asmProp.AssemblyName }

    $versionProp = $pgList | Where-Object { $_.Version -or $_.VersionPrefix } | Select-Object -First 1
    $version = if ($versionProp.Version) { $versionProp.Version } elseif ($versionProp.VersionPrefix) { $versionProp.VersionPrefix } else { "1.0.0.0" }

    # ProjectReference（用于把库归属到引用它的模块）
    $projRefs = @()
    foreach ($ig in @($csprojContent.Project.ItemGroup)) {
        foreach ($pr in @($ig.ProjectReference)) {
            if (-not $pr) { continue }
            if (-not $pr.Include) { continue }
            $inc = [string]$pr.Include
            if ($inc -match '\$') { continue }
            $incNorm = $inc.Replace('\', [System.IO.Path]::DirectorySeparatorChar).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
            try {
                $projRefs += Get-ComparablePath ([System.IO.Path]::Combine((Get-Item -LiteralPath $projDir).FullName, $incNorm))
            } catch { }
        }
    }

    # 扫描源码里的特性文本
    $attrHits = @()
    foreach ($csFile in @(Get-ProjectSourceFile -ProjectDir $projDir)) {
        if ($csFile.Length -gt 4194304) { continue }
        $text = Remove-CsComment (Get-Content -LiteralPath $csFile.FullName -Raw -Encoding UTF8)
        foreach ($m in $attributeRegex.Matches($text)) {
            $attrHits += @{
                Name = Get-AttributeSimpleName ([string]$m.Groups['name'].Value)
                Args = [string]$m.Groups['args'].Value
            }
        }
    }

    $coreHit = @($attrHits | Where-Object { Test-AttributeName $_.Name 'CoreModule' }) | Select-Object -First 1
    $reqHit  = @($attrHits | Where-Object { Test-AttributeName $_.Name 'RequiresCoreModule' }) | Select-Object -First 1
    $metaHit = @($attrHits | Where-Object { Test-AttributeName $_.Name 'PluginMetadata' }) | Select-Object -First 1
    # 版本优先取 [PluginMetadata("Name","Version",...)]：csproj 的 <Version> 常常没跟着更新
    # （实测 MyPotWorld：csproj 是 0.1.0，而 PluginMetadata 是 0.5.0）
    # 元数据参数形如 ("名字", "版本", "作者", "描述")，按引号切分后第 4 段就是版本（不写正则，避免转义踩坑）
    if ($metaHit -and $metaHit.Args) {
        $parts = $metaHit.Args -split '"'
        if ($parts.Count -ge 4 -and -not [string]::IsNullOrWhiteSpace($parts[3])) { $version = $parts[3].Trim() }
    }


    $role = 'Library'
    $module = ''
    if ($coreHit) {
        $role = 'Core'
        $module = $asmName
    } elseif ($reqHit) {
        # 取参数里第一个字符串字面量作为模块名，例如 RequiresCoreModule("VirtualWorldCore")
        $declared = ''
        $qm = [regex]::Match([string]$reqHit.Args, '"((?:[^"\\]|\\.)*)"')
        if ($qm.Success) { $declared = $qm.Groups[1].Value.Trim() }
        if ($declared) {
            $role = 'Gameplay'
            $module = $declared
        } else {
            Write-Warning "[$asmName] RequiresCoreModule 没有模块名参数，按普通插件处理"
            $role = 'Plugin'
        }
    } elseif ($metaHit) {
        $role = 'Plugin'
    }

    $suffix = ''
    if ($module) { $suffix = " module=$module" }
    Write-Host "  $asmName : role=$role$suffix"

    $projectInfos += @{
        Path         = $projPath
        Dir          = $projDir
        FileName     = $projFileName
        AssemblyName = $asmName
        Version      = [string]$version
        Comparable   = Get-ComparablePath ((Get-Item -LiteralPath $projPath).FullName)
        RefFull      = $projRefs
        Role         = $role
        Module       = $module
        HasMetadata  = [bool]$metaHit
    }
}

# Step 4: 库归属模块 —— 优先 ProjectReference，其次名字前缀兜底
$moduleNames = @{}
foreach ($pi in $projectInfos) {
    if ($pi.Role -eq 'Core' -and $pi.Module) { $moduleNames[$pi.Module] = $true }
}

foreach ($pi in $projectInfos) {
    if ($pi.Role -ne 'Library') { continue }
    $candidates = @()
    foreach ($owner in $projectInfos) {
        if (-not $owner.Module) { continue }
        if ($owner.Role -ne 'Core' -and $owner.Role -ne 'Gameplay') { continue }
        if ($owner.RefFull -contains $pi.Comparable) { $candidates += $owner.Module }
    }
    $candidates = @($candidates | Sort-Object -Unique)
    if ($candidates.Count -eq 1) {
        $pi.Module = $candidates[0]
        Write-Host "  Library $($pi.AssemblyName) -> $($pi.Module)/lib (ProjectReference)"
    } else {
        if ($candidates.Count -gt 1) {
            Write-Warning "[$($pi.AssemblyName)] 被多个模块引用（$($candidates -join ', ')），改用名字前缀判断"
        }
        $fallback = Get-ModuleNameByPrefix -AssemblyName $pi.AssemblyName -ModuleNames $moduleNames
        if ($fallback) {
            $pi.Module = $fallback
            Write-Host "  Library $($pi.AssemblyName) -> $($pi.Module)/lib (名字前缀)"
        } else {
            Write-Warning "[$($pi.AssemblyName)] 是库（没有 [PluginMetadata]），但无法判断所属模块；临时放到 publish/ 根目录，且不会写进 Plugins.json"
        }
    }
}

# Step 5: 计算每个程序集在包内的目标目录（相对 publish/）
$publishRoot = "./publish"
$destByAssembly = @{}
foreach ($pi in $projectInfos) {
    $rel = ''
    if ($pi.Role -eq 'Core' -or $pi.Role -eq 'Gameplay') {
        $rel = $pi.Module
    } elseif ($pi.Role -eq 'Library' -and $pi.Module) {
        $rel = Join-Path $pi.Module 'lib'
    }
    $destByAssembly[$pi.AssemblyName] = $rel
}

# Step 6: 生成 Plugins.json（只列真正的插件；库不出现）
Write-Host "Generating Plugins.json..."
$plugins = @()
foreach ($pi in $projectInfos) {
    if (-not $pi.HasMetadata) { continue }

    $descriptions = @{}
    $manifestPath = Join-Path $pi.Dir "manifest.json"
    if (Test-Path $manifestPath) {
        try {
            # 注意：这里刻意不用 ConvertFrom-Json -AsHashtable，保证 PowerShell 5.1 / 7 都能跑
            $manifest = (Get-Content $manifestPath -Raw -Encoding UTF8) | ConvertFrom-Json
            $descZh = $manifest.README.Description
            if ($descZh) { $descriptions["zh-CN"] = $descZh }
            $descEn = $manifest.'README.en-US'.Description
            if ($descEn) { $descriptions["en-US"] = $descEn }
        } catch {
            Write-Warning "Cannot parse manifest at $manifestPath, skipping..."
        }
    }

    $dependencies = @()
    if ($pi.Role -eq 'Gameplay' -and $pi.Module) { $dependencies = @("$($pi.Module).dll") }

    $relPath = "$($pi.AssemblyName).dll"
    if ($pi.Module) { $relPath = "$($pi.Module)/$($pi.AssemblyName).dll" }

    Write-Host "  Plugin: $($pi.AssemblyName) v$($pi.Version) module='$($pi.Module)' path='$relPath'"

    $plugins += [ordered]@{
        Name         = $pi.AssemblyName
        Version      = $pi.Version
        Author       = "Zykor-Club"
        Description  = $descriptions
        AssemblyName = $pi.AssemblyName
        Path         = $relPath
        Dependencies = $dependencies
        HotReload    = $true
        Module       = $pi.Module
    }
}

Set-Content -Path "./publish/Plugins.json" -Value ($plugins | ConvertTo-Json -Depth 3) -Encoding UTF8
Write-Host "Plugins.json generated with $($plugins.Count) plugins"

# Step 7: Copy plugin DLLs / PDBs
# UTSL plugins only ship their own DLL: UnifierTSL.dll / TShockAPI.dll and friends are provided by the host.
#   模块本体 / 族内玩法 -> publish/<模块名>/，库 -> publish/<模块名>/lib/，普通插件 -> publish/ 根
$outDir = "./out/$BuildType"
if (-not (Test-Path $outDir)) {
    Write-Warning "Build output directory '$outDir' not found"
} else {
    $copied = @{}
    foreach ($dll in @(Get-ChildItem -Path $outDir -Filter *.dll -ErrorAction SilentlyContinue)) {
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($dll.Name)
        if (-not $destByAssembly.ContainsKey($baseName)) { continue }
        $relDir = $destByAssembly[$baseName]
        $destDir = $publishRoot
        if ($relDir) { $destDir = Join-Path $publishRoot $relDir }
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        Copy-Item -Path $dll.FullName -Destination $destDir -Force
        $pdbFile = [System.IO.Path]::Combine($dll.DirectoryName, "$baseName.pdb")
        if (Test-Path $pdbFile) { Copy-Item -Path $pdbFile -Destination $destDir -Force }
        $copied[$baseName] = $true
        $shownDir = 'publish/'
        if ($relDir) { $shownDir = "publish/" + $relDir.Replace('\', '/') + "/" }
        Write-Host "  + $baseName.dll -> $shownDir"
    }
    foreach ($name in @($destByAssembly.Keys | Sort-Object)) {
        if (-not $copied.ContainsKey($name)) {
            Write-Warning "out/$BuildType 里没有找到 $name.dll（项目在 slnx 中，但没有产出该程序集）"
        }
    }
}

# Step 8: Copy per-project README / LICENSE
#   命名规则不变（<项目文件名>.README.md / <项目文件名>.LICENSE）；
#   但属于某个模块的项目改放到 publish/<模块名>/ 下，避免堆在根目录。
foreach ($pi in $projectInfos) {
    $destDir = $publishRoot
    if ($pi.Module) { $destDir = Join-Path $publishRoot $pi.Module }
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    foreach ($readme in @(Get-ChildItem -Path $pi.Dir -Filter "README*" -ErrorAction SilentlyContinue)) {
        $readmeName = [System.IO.Path]::GetFileName($readme)
        Copy-Item -Path $readme.FullName -Destination (Join-Path $destDir "$($pi.FileName).$readmeName") -Force -ErrorAction SilentlyContinue
    }
    $licensePath = Join-Path $pi.Dir "LICENSE"
    if (Test-Path $licensePath) {
        Copy-Item -Path $licensePath -Destination (Join-Path $destDir "$($pi.FileName).LICENSE") -Force -ErrorAction SilentlyContinue
    }
}

# Step 9: Create Plugins.zip
if (-not $NoZip) {
    Remove-Item ./out/Plugins.zip -ErrorAction SilentlyContinue
    if (@(Get-ChildItem ./publish/* -ErrorAction SilentlyContinue).Count -eq 0) {
        Write-Warning "Publish directory is empty, creating minimal archive"
        "[]" | Set-Content "./publish/Plugins.json" -Encoding UTF8
    }
    Compress-Archive -Path ./publish/* -DestinationPath ./out/Plugins.zip -Force
    Write-Host "Plugins.zip created! Size: $([math]::Round((Get-Item ./out/Plugins.zip).Length / 1KB, 1)) KB"
}

Write-Host "Publish complete! Output: ./publish/"
