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

# Step 3: Collect plugin metadata from Plugin.slnx
$solution = [xml](Get-Content $solutionPath -Raw)
$allProjects = @($solution.Solution.Project)
$projects = @($allProjects | Where-Object { $_.Path -like "src/*/*.csproj" -and $_.Path -notlike "src/Shared/*" })

Write-Host "Generating Plugins.json..."
$plugins = @()
$assemblyNames = @{}

foreach ($proj in $projects) {
    $projPath = $proj.Path
    $projDir = [System.IO.Path]::GetDirectoryName($projPath)
    $asmName = [System.IO.Path]::GetFileNameWithoutExtension($projPath)

    try {
        $csprojContent = [xml](Get-Content $projPath -Raw)
    } catch {
        Write-Warning "Cannot parse $projPath, skipping..."
        continue
    }

    $pgList = @($csprojContent.Project.PropertyGroup)
    $asmProp = $pgList | Where-Object { $_.AssemblyName } | Select-Object -First 1
    if ($asmProp) { $asmName = $asmProp.AssemblyName }

    $versionProp = $pgList | Where-Object { $_.Version -or $_.VersionPrefix } | Select-Object -First 1
    $version = if ($versionProp.Version) { $versionProp.Version } elseif ($versionProp.VersionPrefix) { $versionProp.VersionPrefix } else { "1.0.0.0" }

    $assemblyNames[$asmName] = $true
    Write-Host "  Plugin: $asmName v$version"

    $descriptions = @{}
    $manifestPath = Join-Path $projDir "manifest.json"
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

    $plugins += @{
        Name = $asmName
        Version = $version
        Author = "Zykor-Club"
        Description = $descriptions
        AssemblyName = $asmName
        Path = "$asmName.dll"
        Dependencies = @()
        HotReload = $true
    }
}

Set-Content -Path "./publish/Plugins.json" -Value ($plugins | ConvertTo-Json -Depth 3) -Encoding UTF8
Write-Host "Plugins.json generated with $($plugins.Count) plugins"

# Step 4: Copy plugin DLLs / PDBs
# UTSL plugins only ship their own DLL: UnifierTSL.dll / TShockAPI.dll and friends are provided by the host.
$outDir = "./out/$BuildType"
if (-not (Test-Path $outDir)) {
    Write-Warning "Build output directory '$outDir' not found"
} else {
    foreach ($dll in @(Get-ChildItem -Path $outDir -Filter *.dll -ErrorAction SilentlyContinue)) {
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($dll.Name)
        if ($assemblyNames.ContainsKey($baseName)) {
            Copy-Item -Path $dll.FullName -Destination ./publish/ -Force
            $pdbFile = [System.IO.Path]::Combine($dll.DirectoryName, "$baseName.pdb")
            if (Test-Path $pdbFile) { Copy-Item -Path $pdbFile -Destination ./publish/ -Force }
            Write-Host "  + $baseName.dll"
        }
    }
}

# Step 5: Copy per-plugin README / LICENSE
foreach ($proj in $projects) {
    $projDir2 = [System.IO.Path]::GetDirectoryName($proj.Path)
    $projFileName = [System.IO.Path]::GetFileNameWithoutExtension($proj.Path)
    foreach ($readme in @(Get-ChildItem -Path $projDir2 -Filter "README*" -ErrorAction SilentlyContinue)) {
        Copy-Item -Path $readme.FullName -Destination "./publish/$projFileName.$([System.IO.Path]::GetFileName($readme))" -Force -ErrorAction SilentlyContinue
    }
    $licensePath = Join-Path $projDir2 "LICENSE"
    if (Test-Path $licensePath) {
        Copy-Item -Path $licensePath -Destination "./publish/$projFileName.LICENSE" -Force -ErrorAction SilentlyContinue
    }
}

# Step 6: Create Plugins.zip
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
