# RuYouMusic 取链网关（自建取链服务）

对外就是一个和公共接口同形的 `/api.php`，服务端插件把它当 `apiBase` 指过来即可。

## 为什么需要它

公共 API 已经退化：`music-api-cn/hk/us` 节点 DNS 已不存在、国际版只支持 netease+joox（joox 直链为空），
而网页 `music.gdstudio.org` 用的它自己的同源 API 有 Cloudflare + 私有签名，非浏览器请求一律 401。

本网关的 **browser 模式**用本机 Edge 无头打开站点，**在页面里用它自己的 `crc32` 和它自己的会话**调它的 `/api.php`
—— Cloudflare 与签名都自然通过，于是能拿到站点后端才有能力解析的直链。

## 运行

```powershell
# 默认 auto：先试 browser，失败回退 public
powershell -ExecutionPolicy Bypass -File .\start-gateway.ps1
# 只走浏览器
powershell -ExecutionPolicy Bypass -File .\start-gateway.ps1 -Mode browser
# 只走公共 API（不需要浏览器）
powershell -ExecutionPolicy Bypass -File .\start-gateway.ps1 -Mode public
```

自检：
```powershell
curl "http://127.0.0.1:7880/health"
curl "http://127.0.0.1:7880/api.php?types=url&source=netease&id=186016&br=320"
```

## 接到点歌机

```powershell
# 插件指向网关（签名由网关在页面里补，所以 apiSign=0）
curl "http://127.0.0.1:7878/music/config?token=你的REST令牌&apiBase=http://127.0.0.1:7880/api.php&apiSign=0"
```

## 参数

| 参数 | 默认 | 说明 |
|:--|:--|:--|
| `--port` | 7880 | 监听端口（调试端口 = port+1） |
| `--mode` | auto | browser / public / auto |
| `--edge` | Program Files (x86) 下的 msedge.exe | 浏览器路径 |
| `--site` | https://music.gdstudio.org/ | 站点入口 |
| `--publicApi` | https://music-api.gdstudio.xyz/api.php | public 模式的上游 |

## 说明

- 零依赖（Node 24 自带 fetch/WebSocket）；
- 结果带 TTL 缓存（url 12 分钟 / search 5 分钟 / lyric 1 小时），命中会带 `X-Gateway-Cache: HIT`；
- 浏览器模式串行执行（单页面），并发请求会排队；
- 仅供个人自用；站点接口与签名属于对方，请勿公开分发或商用。
