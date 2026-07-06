using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Recorder.Capture;
using Recorder.Encoding;
using Recorder.Recording;
using Recorder.Telemetry;
using Recorder.Windows;
using System;
using System.Threading;

namespace Recorder;

public sealed class Plugin : IDalamudPlugin
{
    internal const string CommandName = "/pocketrecorder";
    internal const string ShortCommandName = "/pktr";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;

    internal Configuration Config { get; }
    internal IRecorderEnvironment Environment { get; }
    internal GameGraphicsDeviceProbeCache GameGraphicsDeviceProbeCache { get; }
    internal RecordingService RecordingService { get; }
    internal RecordingRetentionCleanupService RetentionCleanupService { get; }
    internal AutoDutyRecordingService AutoDutyRecordingService { get; }
    internal ConfigWindow ConfigWindow { get; }
    internal FloatingRecordWindow FloatingRecordWindow { get; }

    internal Dalamud.Interface.Windowing.WindowSystem WindowSystem { get; }
    internal volatile bool IsFFmpegBootstrapRunning;
    internal volatile bool IsFFmpegBootstrapComplete;
    internal string FFmpegBootstrapStatus { get; private set; } = string.Empty;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        NativeRecorderRuntimeManager.ConfigureFromPluginInterface(pluginInterface);
        Environment = new DalamudRecorderEnvironment(pluginInterface, Log);
        Config = Configuration.Load(pluginInterface);
        PocketBackendClient.Configure(Config);

        GameGraphicsDeviceProbeCache = new GameGraphicsDeviceProbeCache(Framework, Environment.Log);
        RecordingService = new RecordingService(this, GameInterop, Environment);
        RetentionCleanupService = new RecordingRetentionCleanupService(this, Environment);
        AutoDutyRecordingService = new AutoDutyRecordingService(this, ClientState, DutyState, Framework);
        ConfigWindow = new ConfigWindow(this);
        FloatingRecordWindow = new FloatingRecordWindow(this);

        WindowSystem = new Dalamud.Interface.Windowing.WindowSystem("Pocket Recorder");
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(FloatingRecordWindow);

        PluginInterface.UiBuilder.Draw += OnUiBuilderDraw;
        PluginInterface.UiBuilder.OpenConfigUi += () => ConfigWindow.IsOpen = true;

        AddCommandHandler(CommandName);
        AddCommandHandler(ShortCommandName);

        StartBackgroundWarmup();
        GameGraphicsDeviceProbeCache.StartWarmup();
        PocketBackendClient.QueueHeartbeat("startup", new
        {
            targetFps = Config.TargetFps,
            audio = Config.AudioCaptureMode.ToString(),
            autoRecord = Config.AutoRecordEightPlayerDuty,
        });
    }

    private void StartBackgroundWarmup()
    {
        var thread = new Thread(() =>
        {
            try
            {
                IsFFmpegBootstrapRunning = true;
                FFmpegBootstrapStatus = "正在检查 FFmpeg...";

                string pluginConfigDirectory = PluginInterface.GetPluginConfigDirectory();
                string? ffmpegPath = FFmpegBootstrapper.TryResolveExistingPath(
                    Config.FFmpegPath,
                    pluginConfigDirectory);

                if (ffmpegPath == null)
                {
                    FFmpegBootstrapStatus = "正在下载必要组件...";
                    ffmpegPath = FFmpegBootstrapper.InstallOrUpdateBundled(pluginConfigDirectory);
                    Config.FFmpegPath = ffmpegPath;
                    Config.Save(PluginInterface);
                }

                if (ffmpegPath == null)
                {
                    FFmpegBootstrapStatus = "必要组件下载失败";
                    return;
                }

                FFmpegEncoderSelector.Warmup(ffmpegPath, Config);
                FFmpegBootstrapStatus = "必要组件已就绪";
                IsFFmpegBootstrapComplete = true;
                Log.Info($"[FFmpeg] Background warmup finished: {ffmpegPath}");
            }
            catch (Exception ex)
            {
                FFmpegBootstrapStatus = "必要组件下载失败";
                Log.Warning($"[FFmpeg] Background warmup failed: {ex.Message}");
            }
            finally
            {
                IsFFmpegBootstrapRunning = false;
            }
        })
        {
            IsBackground = true,
            Name = "Recorder-FFmpegWarmup",
        };

        thread.Start();
    }

    private void AddCommandHandler(string commandName)
    {
        CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Pocket Recorder: start, end, toggle, status, autorecord on/off/toggle, floating on/off/toggle, fps, bitrate, audio, overlay, output, config, help。",
        });
    }

    private void OnCommand(string command, string args)
    {
        string trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
            return;
        }

        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string action = parts[0].ToLowerInvariant();

        switch (action)
        {
            case "start":
            case "begin":
                StartRecordingFromCommand();
                break;

            case "end":
            case "stop":
                StopRecordingFromCommand();
                break;

            case "toggle":
                RecordingService.ToggleRecording();
                Print($"录制状态: {RecordingService.Phase.ToDisplayText()}");
                break;

            case "status":
            case "state":
                PrintStatus();
                break;

            case "autorecord":
            case "auto":
                HandleAutoRecordCommand(parts);
                break;

            case "floating":
            case "float":
            case "icon":
                HandleFloatingCommand(parts);
                break;

            case "fps":
                HandleFpsCommand(parts);
                break;

            case "bitrate":
            case "br":
                HandleBitrateCommand(parts);
                break;

            case "audio":
                HandleAudioCommand(parts);
                break;

            case "overlay":
            case "ui":
            case "imgui":
                HandleOverlayCommand(parts);
                break;

            case "output":
            case "folder":
            case "dir":
                OpenOutputDirectory();
                break;

            case "config":
            case "settings":
            case "open":
                ConfigWindow.IsOpen = true;
                Print("已打开设置窗口。");
                break;

            case "help":
            case "?":
                PrintHelp();
                break;

            default:
                Print($"未知指令: {parts[0]}");
                PrintHelp();
                break;
        }
    }

    private void StartRecordingFromCommand()
    {
        var phase = RecordingService.Phase;
        if (phase != RecordingPhase.Idle)
        {
            Print($"无法开始录制，当前状态: {phase.ToDisplayText()}。");
            return;
        }

        Print(RecordingService.StartRecording()
            ? "开始录制。"
            : "开始录制失败，请查看日志。");
    }

    private void StopRecordingFromCommand()
    {
        var phase = RecordingService.Phase;
        if (phase == RecordingPhase.Idle)
        {
            Print("当前没有正在进行的录制。");
            return;
        }

        if (phase == RecordingPhase.Finalizing)
        {
            Print("录制正在保存中。");
            return;
        }

        RecordingService.StopRecording();
        Print("停止录制，正在保存。");
    }

    private void HandleAutoRecordCommand(string[] parts)
    {
        HandleSwitchCommand(
            parts,
            Config.AutoRecordEightPlayerDuty,
            $"倒计时自动录制: {OnOff(Config.AutoRecordEightPlayerDuty)}。{AutoDutyRecordingService.StatusText}",
            "用法: autorecord on | off | toggle | status",
            enabled => Config.AutoRecordEightPlayerDuty = enabled,
            enabled => $"倒计时自动录制已{(enabled ? "开启" : "关闭")}。");
    }

    private void HandleFloatingCommand(string[] parts)
    {
        HandleSwitchCommand(
            parts,
            Config.ShowFloatingRecordButton,
            $"悬浮录制按钮: {OnOff(Config.ShowFloatingRecordButton)}。",
            "用法: floating on | off | toggle | status",
            enabled =>
            {
                Config.ShowFloatingRecordButton = enabled;
                FloatingRecordWindow.IsOpen = enabled;
            },
            enabled => $"悬浮录制按钮已{(enabled ? "显示" : "隐藏")}。");
    }

    private void HandleFpsCommand(string[] parts)
    {
        if (parts.Length == 1 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Print($"目标帧率: {Config.TargetFps} FPS。");
            return;
        }

        if (!int.TryParse(parts[1], out int fps) || fps is < 15 or > 144)
        {
            Print("用法: fps 15-144");
            return;
        }

        Config.TargetFps = fps;
        Config.Save(PluginInterface);
        Print($"目标帧率已设为 {fps} FPS，下次录制生效。");
    }

    private void HandleBitrateCommand(string[] parts)
    {
        if (parts.Length == 1 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Print($"视频码率: {Config.VideoBitrate / 1_000_000} Mbps。");
            return;
        }

        if (!int.TryParse(parts[1], out int bitrateMbps) || bitrateMbps is < 1 or > 100)
        {
            Print("用法: bitrate 1-100");
            return;
        }

        Config.VideoBitrate = bitrateMbps * 1_000_000;
        Config.Save(PluginInterface);
        Print($"视频码率已设为 {bitrateMbps} Mbps，下次录制生效。");
    }

    private void HandleAudioCommand(string[] parts)
    {
        if (parts.Length == 1 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Print($"声音来源: {AudioModeText(Config.AudioCaptureMode)}。");
            return;
        }

        AudioCaptureMode? mode = ParseAudioMode(parts[1], Config.AudioCaptureMode);
        if (mode == null)
        {
            Print("用法: audio game | system | off | toggle | status");
            return;
        }

        Config.AudioCaptureMode = mode.Value;
        Config.CaptureAudio = mode.Value != AudioCaptureMode.Off;
        Config.Save(PluginInterface);
        Print($"声音来源已设为 {AudioModeText(mode.Value)}，下次录制生效。");
    }

    private void OpenOutputDirectory()
    {
        string dir = Config.GetEffectiveOutputDirectory(PluginInterface);
        try
        {
            ShellHelpers.OpenDirectory(dir);
            Print($"已打开输出目录: {dir}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open output directory: {ex}");
            Print($"打开输出目录失败: {ex.Message}");
        }
    }

    private void PrintStatus()
    {
        string elapsed = RecordingService.Phase == RecordingPhase.Recording
            ? $"，时长 {RecordingService.Elapsed:hh\\:mm\\:ss}，帧数 {RecordingService.FrameCount}"
            : string.Empty;

        Print($"录制状态: {RecordingService.Phase.ToDisplayText()}{elapsed}。");
        Print($"倒计时自动录制: {OnOff(Config.AutoRecordEightPlayerDuty)}。{AutoDutyRecordingService.StatusText}");
        Print($"参数: {Config.TargetFps} FPS / {Config.VideoBitrate / 1_000_000} Mbps / 声音 {AudioModeText(Config.AudioCaptureMode)} / 卫月界面 {OnOff(Config.IncludeOverlay)}。");
    }

    private void HandleOverlayCommand(string[] parts)
    {
        HandleSwitchCommand(
            parts,
            Config.IncludeOverlay,
            $"录制卫月界面: {OnOff(Config.IncludeOverlay)}。",
            "用法: overlay on | off | toggle | status",
            enabled => Config.IncludeOverlay = enabled,
            enabled => $"录制卫月界面已{(enabled ? "开启" : "关闭")}，下次录制生效。");
    }

    private void HandleSwitchCommand(
        string[] parts,
        bool currentValue,
        string statusText,
        string usageText,
        Action<bool> apply,
        Func<bool, string> changedText)
    {
        if (parts.Length == 1 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Print(statusText);
            return;
        }

        bool? enabled = ParseSwitch(parts[1], currentValue);
        if (enabled == null)
        {
            Print(usageText);
            return;
        }

        apply(enabled.Value);
        Config.Save(PluginInterface);
        Print(changedText(enabled.Value));
    }

    private static bool? ParseSwitch(string value, bool currentValue)
    {
        return value.ToLowerInvariant() switch
        {
            "on" or "enable" or "enabled" or "1" or "true" or "yes" => true,
            "off" or "disable" or "disabled" or "0" or "false" or "no" => false,
            "toggle" => !currentValue,
            _ => null,
        };
    }

    private static string OnOff(bool enabled) => enabled ? "开启" : "关闭";

    private static AudioCaptureMode? ParseAudioMode(string value, AudioCaptureMode currentValue)
    {
        return value.ToLowerInvariant() switch
        {
            "game" or "ffxiv" or "process" or "on" or "enable" or "enabled" or "1" or "true" or "yes" => AudioCaptureMode.Game,
            "system" or "all" or "desktop" or "loopback" => AudioCaptureMode.System,
            "off" or "none" or "mute" or "disable" or "disabled" or "0" or "false" or "no" => AudioCaptureMode.Off,
            "toggle" => currentValue == AudioCaptureMode.Off ? AudioCaptureMode.Game : AudioCaptureMode.Off,
            _ => null,
        };
    }

    private static string AudioModeText(AudioCaptureMode mode)
    {
        return mode switch
        {
            AudioCaptureMode.Game => "只录制游戏声音",
            AudioCaptureMode.System => "录制系统声音",
            _ => "不录制声音",
        };
    }

    private static void Print(string message)
    {
        ChatGui.Print($"[Pocket Recorder] {message}");
    }

    private static void PrintHelp()
    {
        Print("命令: start, end, toggle, status, config, output");
        Print("自动录制: autorecord on/off/toggle/status");
        Print("悬浮按钮: floating on/off/toggle/status");
        Print("参数: fps 60, bitrate 32, audio game/system/off/status, overlay on/off/status");
        Print("短命令同样可用: /pktr start, /pktr end");
    }

    private void OnUiBuilderDraw()
    {
        WindowSystem.Draw();
    }

    public void Dispose()
    {
        RetentionCleanupService.Dispose();
        AutoDutyRecordingService.Dispose();
        RecordingService.Dispose();
        GameGraphicsDeviceProbeCache.Dispose();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommandName);
        PluginInterface.UiBuilder.Draw -= OnUiBuilderDraw;
        WindowSystem.RemoveWindow(FloatingRecordWindow);
        WindowSystem.RemoveWindow(ConfigWindow);
    }
}
