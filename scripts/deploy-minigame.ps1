# =====================================================================
#  deploy-minigame.ps1 —— 把 MiniGameCore + Parkour 部署到服务端
#
#  背景（2026-10-24 踩过的坑）：
#    宿主 ModuleAssemblyLoader.PreloadModules 用 Dictionary<string, FileInfo> 按【文件名】去重，
#    plugins 根目录的文件后写入 → 会覆盖子目录里的同名 dll。
#    结果就是"修好→部署→没变化"，其实加载的一直是根目录那份旧 dll。
#
#  所以本脚本做三件事：
#    1) 先删掉 plugins 根目录里的同名 dll（只允许放在插件子目录）
#    2) 按固定布局复制：plugins\VirtualWorldCore\{MiniGameCore,Parkour}.dll + lib\*.dll
#    3) 打印校验（哈希 + 最终位置），确认没有第二份
#
#  用法：powershell -File scripts\deploy-minigame.ps1 [-DeployRoot <utsl-deploy 路径>]
# =====================================================================
param(
  [string]$DeployRoot = "C:\Users\星梦\Desktop\01_UTSL平台\utsl-deploy"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo "out\Release"
$pluginDir = Join-Path $DeployRoot "plugins\VirtualWorldCore"
$libDir = Join-Path $pluginDir "lib"

if (-not (Test-Path $out)) { throw "找不到构建输出: $out（先 dotnet build Plugin.slnx -c Release）" }
if (-not (Test-Path $pluginDir)) { New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null }

# ---------- 1) 清掉 plugins 根目录里的同名副本 ----------
$strays = @()
foreach ($name in @("MiniGameCore.dll", "Parkour.dll", "MiniGameCore.Abstractions.dll", "VirtualWorldCore.Abstractions.dll")) {
  $p = Join-Path (Join-Path $DeployRoot "plugins") $name
  if (Test-Path $p) { Remove-Item $p -Force; $strays += $name }
}
if ($strays.Count -gt 0) { Write-Host ("已清理 plugins 根目录同名副本: " + ($strays -join ", ")) -ForegroundColor Yellow }
else { Write-Host "plugins 根目录没有同名副本" }

# ---------- 2) 复制 ----------
# ★ 实测：Abstractions 必须放在插件根目录（放 lib\ 会导致服务端起不来）
#   两个插件 + VirtualWorldCore 同处一个文件夹，依赖也放同一层，保证是同一份程序集实例
$map = @(
  @{ Src = "MiniGameCore.dll";               Dst = $pluginDir },
  @{ Src = "Parkour.dll";                    Dst = $pluginDir },
  @{ Src = "MiniGameCore.pdb";               Dst = $pluginDir },
  @{ Src = "Parkour.pdb";                    Dst = $pluginDir },
  @{ Src = "MiniGameCore.Abstractions.dll";  Dst = $pluginDir },
  @{ Src = "VirtualWorldCore.Abstractions.dll"; Dst = $pluginDir }
)
foreach ($m in $map) {
  $s = Join-Path $out $m.Src
  if (-not (Test-Path $s)) { Write-Host ("跳过（不存在）: " + $m.Src) -ForegroundColor DarkGray; continue }
  Copy-Item $s $m.Dst -Force
}

# ---------- 3) 校验 ----------
Write-Host "`n=== 部署结果 ==="
Get-ChildItem $pluginDir -Recurse -File -Include *.dll |
  Select-Object @{n='rel';e={$_.FullName.Replace($pluginDir+'\','')}},@{n='KB';e={[int]($_.Length/1KB)}},LastWriteTime |
  Sort-Object rel | Format-Table -AutoSize

$dupes = Get-ChildItem (Join-Path $DeployRoot "plugins") -Recurse -File -Include "MiniGameCore.dll","Parkour.dll" |
         Where-Object { $_.DirectoryName -ne $pluginDir }
if ($dupes) {
  Write-Host "`n!! 仍有副本（会导致加载到旧版本）：" -ForegroundColor Red
  $dupes | Select-Object FullName | Format-Table -AutoSize
  exit 1
}

foreach ($n in @("MiniGameCore.dll", "Parkour.dll", "MiniGameCore.Abstractions.dll", "VirtualWorldCore.Abstractions.dll")) {
  $dep = Join-Path $pluginDir $n
  $src = Join-Path $out $n
  $h1 = (Get-FileHash $dep).Hash
  $h2 = (Get-FileHash $src).Hash
  $ok = $h1 -eq $h2
  Write-Host ("{0,-22} 与构建一致={1}  hash={2}" -f $n, $ok, $h1.Substring(0, 12)) -ForegroundColor ($(if ($ok) { "Green" } else { "Red" }))
  if (-not $ok) { exit 1 }
}

Write-Host "`n部署完成。重启服务端：管理器「停止」→「启动」；然后看日志里的 [登记] 玩法 parkour" -ForegroundColor Cyan
