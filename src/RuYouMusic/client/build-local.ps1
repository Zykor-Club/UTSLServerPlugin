# Build the client plugin locally (edit the two paths if yours differ).
param(
  [string]$GameDir   = "D:\Steam\steamapps\common\Terraria",
  [string]$LoaderDir = "C:\Users\星梦\Desktop\点歌机逆向\inflated程序集"
)
if (-not (Test-Path (Join-Path $LoaderDir "R.PluginSwitch.dll"))) {
  Write-Error "R.PluginSwitch.dll not found in $LoaderDir - inflate it from the game's Plugins\R.PluginSwitch.rym first"
  exit 1
}
dotnet build -c Release -p:GameDir="$GameDir" -p:LoaderDir="$LoaderDir"
if ($LASTEXITCODE -eq 0) {
  Write-Output ""
  Write-Output "OK: .\bin\Release\RuYouMusicClient.dll"
  Write-Output "Install: copy it into $GameDir\Plugins\ (or run .\pack-rym.ps1 and copy the .rym)"
}
