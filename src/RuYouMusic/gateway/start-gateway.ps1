# 启动取链网关（默认浏览器模式：用本机 Edge 过 Cloudflare + 站点自己的签名）
param([int]$Port = 7880, [string]$Mode = "auto")
node "$PSScriptRoot\gateway.js" --port $Port --mode $Mode
