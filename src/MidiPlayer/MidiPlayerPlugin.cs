using System.Collections.Immutable;
using System.Reflection;
using MidiPlayer.Config;
using MidiPlayer.Core;
using OTAPI;
using Terraria;
using Terraria.ID;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace MidiPlayer;

/// <summary>
/// MidiPlayer（UTSL 版）
///
/// 与 TShock 版的差异（入口相关，Core/Commands 基本原样）：
///   - 入口：TerrariaPlugin + [ApiVersion] -> BasePlugin + [PluginMetadata]
///   - 每帧：ServerApi.Hooks.GameUpdate -> UnifierApi.EventHub.Game.PostUpdate
///     （PostUpdate 在 UTSL 是**每个世界各触发一次**，而 PlayScheduler 是全局单例，
///      所以加了 TickCount 去重，保证每帧只推进一次）
///   - 进服：ServerApi.Hooks.ServerJoin（UTSL 没有）-> PlayerHooks.PlayerPostLogin
///   - 离开：ServerApi.Hooks.ServerLeave -> UnifierApi.EventHub.Netplay.LeaveEvent
///   - 配置目录：TShock.SavePath/MidiPlayer -> IPluginConfigRegistrar.Directory（config/MidiPlayer/）
///   - JSON：Newtonsoft -> System.Text.Json
///   - 命令执行者：args.Player -> (args.Player ?? args.ExecutorActor)
///   - 第三方依赖：Melanchall.DryWetMidi 走 ModuleDependencies + NugetDependency，不打进包
///
/// 保留不动的（UTSL 的 OTAPI 里存在）：
///   - Hooks.MessageBuffer.GetData（屏蔽手弹乐器包）
///   - Terraria.WorldGen.Hooks.OnWorldLoad
///   - GeneralHooks.ReloadEvent
/// </summary>
[PluginMetadata("MidiPlayer", "1.4.5.5", "主要开发：星梦; 协助开发：mountain; 灵感来源与开发指导：ruyou", "通过Terraria播放 MIDI 音乐......")]
public class MidiPlayerPlugin : BasePlugin
{

    private Commands.MidiCommands _midiCommands = null!;



    public override int InitializationOrder => TShock.Order + 1;

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock")
            {
                await initInfo.InitializationTask;
            }
        }

        MidiPlayerPaths.Initialize(configRegistrar.Directory);

        // 1. 初始化 SoundID 索引映射（与上游一致）
        if (SoundID.IndexByName == null)
        {
            SoundID.FillAccessMap();
            TShock.Log.Info("[MidiPlayer] 已初始化 SoundID 访问映射");
        }
        SoundSender.Initialize();

        // 2. 加载配置
        LoadAndApplyConfig();

        // 3. 初始化八音盒绑定
        MusicBoxBinding.Instance.Initialize();

        // 4. 注册 Hooks 和命令
        // 手弹乐器包拦截：上游用 Hooks.MessageBuffer.GetData（UTSL 没有），改用 NetPacketHandler<PlayNote>
        InstrumentSoundInterceptor.Register();
        UnifierApi.EventHub.Game.PostUpdate.Register(OnGameUpdate, HandlerPriority.Normal);
        PlayerHooks.PlayerPostLogin += OnServerJoin;
        UnifierApi.EventHub.Netplay.LeaveEvent.Register(OnServerLeave, HandlerPriority.Normal);
        // 世界加载完成：上游用 WorldGen.Hooks.OnWorldLoad（UTSL 没有），改用 GamePostInitialize
        UnifierApi.EventHub.Game.GamePostInitialize.Register(OnWorldLoad, HandlerPriority.Normal);
        GeneralHooks.ReloadEvent += OnReload;
        _midiCommands = new Commands.MidiCommands();
        _midiCommands.Register(TShockAPI.Commands.ChatCommands);

        TShock.Log.Info("[MidiPlayer] 初始化完成");
    }

    public override ValueTask DisposeAsync(bool isDisposing)
    {
        if (isDisposing)
        {
            InstrumentSoundInterceptor.Unregister();
            UnifierApi.EventHub.Game.PostUpdate.UnRegister(OnGameUpdate);
            PlayerHooks.PlayerPostLogin -= OnServerJoin;
            UnifierApi.EventHub.Netplay.LeaveEvent.UnRegister(OnServerLeave);
            UnifierApi.EventHub.Game.GamePostInitialize.UnRegister(OnWorldLoad);
            GeneralHooks.ReloadEvent -= OnReload;
            _midiCommands?.Unregister(TShockAPI.Commands.ChatCommands);
            MusicBoxBinding.Instance.Dispose();
        }

        return base.DisposeAsync(isDisposing);
    }

    private void LoadAndApplyConfig()
    {
        MidiPlayerConfig config = MidiPlayerConfig.Load();
        TShock.Log.Info("[MidiPlayer] 配置已加载: config/MidiPlayer/MidiPlayerConfig.json");

        // 加载乐器映射表（首次自动生成默认配置）
        InstrumentMapConfig instrumentMap = InstrumentMapConfig.Load();
        InstrumentMapper.Instance = new InstrumentMapper(instrumentMap);

        var songConverter = new SongConverter
        {
            EnableBellHarmony = config.EnableBellHarmony,
            EnableHarpReverb = config.EnableHarpReverb
        };
        config.ApplyTo(songConverter);

        PlayScheduler.Instance.Init(songConverter, config.LookAheadMs);
        PlayScheduler.Instance.MaxNotesPerTick = config.MaxNotesPerTick;
        PlayScheduler.Instance.SameStyleRetriggerMs = config.SameStyleRetriggerMs;
        InstrumentSoundInterceptor.Enabled = config.BlockInstrumentSound;
        InstrumentSoundInterceptor.WarnOnManualPlay = config.WarnOnManualPlay;

    }

    /// <summary>热重载配置（由 TShock /reload 触发）</summary>
    public void ReloadConfig()
    {
        LoadAndApplyConfig();
    }

    /// <summary>
    /// 对应上游 ServerApi.Hooks.GameUpdate。
    /// ⚠️ UTSL 里 PostUpdate 是**按世界**触发的（多世界各一次），而 PlayScheduler 是全局单例，
    /// 所以这里按 TickCount 去重，保证每帧只推进一次调度器。
    /// </summary>
    /// <summary>
    /// 对应上游 ServerApi.Hooks.GameUpdate。
    ///
    /// ⚠️ 不要在这里加时间闸门：TShock 的 GameUpdate 严格每 tick 触发一次，音符因此被均匀摊到
    /// 每个 tick。PostUpdate 同样是「按世界每 tick 一次」，语义一致，直接用即可；
    /// 加闸门反而会把音符挤成突刺。
    /// </summary>
    private void OnGameUpdate(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
    {
        PlayScheduler.Instance.Update();
        MusicBoxBinding.Instance.CheckBindTimeout();
    }

    /// <summary>对应上游 ServerApi.Hooks.ServerJoin（UTSL 没有该事件，用登录完成代替）</summary>
    private void OnServerJoin(PlayerPostLoginEventArgs e)
    {
        if (e.Player is { } player)
            PlayScheduler.Instance.OnPlayerJoin(player.Index);
    }

    /// <summary>对应上游 ServerApi.Hooks.ServerLeave</summary>
    private void OnServerLeave(ref ReadonlyNoCancelEventArgs<LeaveEvent> args)
        => PlayScheduler.Instance.OnPlayerLeave(args.Content.Who);

    private static void OnWorldLoad(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
        => MusicBoxBinding.Instance.OnWorldLoad(args.Content.Server);

    private void OnReload(ReloadEventArgs args) => ReloadConfig();

}
