<div align="center">

# Zykor-Club UTSL Server Plugin

UnifierTSL Plugin Collection Repository

</div>

## Intro
- A UnifierTSL (UTSL) plugin collection repository maintained by ZykorClub members
- Contains plugins ported from TShock plugins to UTSL
- Plugins are continuously updated

## Plugin List

<!--{PluginList,en-US}-->

## Usage
1. Put the `.dll` files into the UTSL server's `plugins` folder
   - ⚠️ **Only ship the plugin's own DLL**, do not copy UnifierTSL.dll / TShockAPI.dll — the host already provides them
2. Restart the UTSL server
3. Check each plugin's README for details

> Legacy TShock plugins (`TerrariaPlugin` + `[ApiVersion]`) **cannot** be used in UTSL directly —
> they must be ported following this repository's pattern.

## Porting a TShock plugin to UTSL

| Item | TShock | UTSL |
|:--|:--|:--|
| Base class | `TerrariaPlugin` + `[ApiVersion(2,1)]` | `BasePlugin` + `[PluginMetadata(...)]` |
| Lifetime | `Initialize()` / `Dispose(bool)` | `InitializeAsync(...)` / `DisposeAsync(bool)` |
| Packet events | `GetDataHandlers.Xxx.Register(...)` | `NetPacketHandler.Register<TPacket>(...)` |
| Server events | `ServerApi.Hooks.*` | `UnifierApi.EventHub.*` / `TShockAPI.Hooks.*` |
| Database | `TShock.DB.Query/QueryReader` | linq2db (`GetTable<T>()` / `CreateTable<T>()`) |
| Logging | `TShock.Log.ConsoleInfo/ConsoleError` | `TShock.Log.Info/Error` (`Warn` -> `Warning`) |
| Config dir | `tshock/` | `config/<PluginName>/` (`IPluginConfigRegistrar.Directory`) |

## Build

Plugin projects reference the UnifierTSL source through `template.targets`, which expects it in the
**sibling directory** `../UnifierTSL` by default:

```bash
git clone https://github.com/CedaryCat/UnifierTSL.git
dotnet build Plugin.slnx -c Release
```

If the UTSL source lives elsewhere:

```bash
dotnet build Plugin.slnx -c Release -p:UnifierTSLRepo=D:\path\to\UnifierTSL
```

Output goes to `out/Release/`.
