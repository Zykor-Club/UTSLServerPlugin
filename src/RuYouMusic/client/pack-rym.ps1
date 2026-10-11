# 把编译好的 RuYouMusicClient.dll 压成 .rym（raw deflate）—— 加载器对 .rym 会先解压再 Assembly.Load
param([string]$Dll = ".\bin\Release\RuYouMusicClient.dll", [string]$Out = ".\RuYouMusicClient.rym")
$in = [IO.File]::ReadAllBytes((Resolve-Path $Dll))
$ms = New-Object IO.MemoryStream
$ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Compress)
$ds.Write($in, 0, $in.Length)
$ds.Close()
[IO.File]::WriteAllBytes($Out, $ms.ToArray())
$ms.Close()
Write-Output ("生成 " + $Out + "  " + (Get-Item $Out).Length + " B（原 dll " + $in.Length + " B）")
