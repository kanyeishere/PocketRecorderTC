using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Recorder.Capture;
using Recorder.Encoding;
using Recorder.Localization;
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
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    internal Configuration Config { get; }
    internal IRecorderEnvironment Environment { get; }
    internal GameGraphicsDeviceProbeCache GameGraphicsDeviceProbeCache { get; }
    internal RecordingService RecordingService { get; }
    internal RecordingRetentionCleanupService RetentionCleanupService { get; }
    internal AutoDutyRecordingService AutoDutyRecordingService { get; }
    internal ConfigWindow ConfigWindow { get; }
    internal FloatingRecordWindow FloatingRecordWindow { get; }
    internal RecordingListWindow RecordingListWindow { get; }

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

        Loc.Initialize(Config.Language, PluginInterface.UiLanguage);

        GameGraphicsDeviceProbeCache = new GameGraphicsDeviceProbeCache(Framework, Environment.Log);
        RecordingService = new RecordingService(this, GameInterop, Environment);
        RetentionCleanupService = new RecordingRetentionCleanupService(this, Environment);
        AutoDutyRecordingService = new AutoDutyRecordingService(this, ClientState, DutyState, Framework);
        ConfigWindow = new ConfigWindow(this);
        FloatingRecordWindow = new FloatingRecordWindow(this);
        RecordingListWindow = new RecordingListWindow(this);

        WindowSystem = new Dalamud.Interface.Windowing.WindowSystem("Pocket Recorder");
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(FloatingRecordWindow);
        WindowSystem.AddWindow(RecordingListWindow);

        PluginInterface.UiBuilder.Draw += OnUiBuilderDraw;
        PluginInterface.UiBuilder.OpenConfigUi += () => ConfigWindow.IsOpen = true;

        AddCommandHandler(CommandName);
        AddCommandHandler(ShortCommandName);

        StartBackgroundWarmup();
        GameGraphicsDeviceProbeCache.StartWarmup();
        RecordingService.StartPresentHookWarmup();
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
                FFmpegBootstrapStatus = Loc.T("Warmup.CheckingFFmpeg");

                string pluginConfigDirectory = PluginInterface.GetPluginConfigDirectory();
                string? ffmpegPath = FFmpegBootstrapper.TryResolveExistingPath(
                    Config.FFmpegPath,
                    pluginConfigDirectory);

                if (ffmpegPath == null)
                {
                    FFmpegBootstrapStatus = Loc.T("Warmup.DownloadingComponents");
                    ffmpegPath = FFmpegBootstrapper.InstallOrUpdateBundled(pluginConfigDirectory);
                    Config.FFmpegPath = ffmpegPath;
                    Config.Save(PluginInterface);
                }

                if (ffmpegPath == null)
                {
                    FFmpegBootstrapStatus = Loc.T("Warmup.Failed");
                    return;
                }

                FFmpegEncoderSelector.Warmup(ffmpegPath, Config);
                FFmpegBootstrapStatus = Loc.T("Warmup.Ready");
                IsFFmpegBootstrapComplete = true;
                Log.Info($"[FFmpeg] Background warmup finished: {ffmpegPath}");
            }
            catch (Exception ex)
            {
                FFmpegBootstrapStatus = Loc.T("Warmup.Failed");
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
            HelpMessage = "Pocket Recorder: start, end, toggle, status, list, autorecord on/off/toggle, floating on/off/toggle, fps, bitrate, audio, overlay, output, config, help。",
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
                Print(Loc.T("Cmd.ToggleStatus", RecordingService.Phase.ToDisplayText()));
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

            case "list":
            case "recordings":
            case "videos":
                                RecordingListWindow.IsOpen = true;
                Print(Loc.T("Cmd.OpenedList"));
                break;

            case "config":
            case "settings":
            case "open":
                                ConfigWindow.IsOpen = true;
                Print(Loc.T("Cmd.OpenedConfig"));
                break;

            case "help":
            case "?":
                PrintHelp();
                break;

            default:
                Print(Loc.T("Cmd.UnknownCommand", parts[0]));
                PrintHelp();
                break;
        }
    }

    private void StartRecordingFromCommand()
    {
        var phase = RecordingService.Phase;
        if (phase != RecordingPhase.Idle)
        {
            Print(Loc.T("Cmd.CannotStartStatus", phase.ToDisplayText()));
            return;
        }

        Print(RecordingService.StartRecording()
            ? Loc.T("Cmd.Started")
            : Loc.T("Cmd.StartFailed"));
    }

    private void StopRecordingFromCommand()
    {
        var phase = RecordingService.Phase;
        if (phase == RecordingPhase.Idle)
        {
            Print(Loc.T("Cmd.NoRecording"));
            return;
        }

        if (phase == RecordingPhase.Finalizing)
        {
            Print(Loc.T("Cmd.SavingRecording"));
            return;
        }

        RecordingService.StopRecording();
        Print(Loc.T("Cmd.StoppedSaving"));
    }

    private void HandleAutoRecordCommand(string[] parts)
    {
        HandleSwitchCommand(
            parts,
            Config.AutoRecordEightPlayerDuty,
            Loc.T("Cmd.AutoRecordStatus", Loc.OnOff(Config.AutoRecordEightPlayerDuty), AutoDutyRecordingService.StatusText),
            Loc.T("Cmd.AutoRecordUsage"),
            enabled => Config.AutoRecordEightPlayerDuty = enabled,
            enabled => Loc.T(enabled ? "Cmd.AutoRecordOn" : "Cmd.AutoRecordOff"));
    }

    private void HandleFloatingCommand(string[] parts)
    {
        HandleSwitchCommand(
            parts,
            Config.ShowFloatingRecordButton,
            Loc.T("Cmd.FloatingStatus", Loc.OnOff(Config.ShowFloatingRecordButton)),
            Loc.T("Cmd.FloatingUsage"),
            enabled =>
            {
                Config.ShowFloatingRecordButton = enabled;
                FloatingRecordWindow.IsOpen = enabled;
            },
            enabled => Loc.T(enabled ? "Cmd.FloatingShown" : "Cmd.FloatingHidden"));
    }

    private void HandleFpsCommand(string[] parts)
    {
        if (parts.Length == 1 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Print(Loc.T("Cmd.FpsStatus", Config.TargetFps));
            return;
        }

        if (!int.TryParse(parts[1], out int fps) || fps is < 15 or > 144)
        {
            Print(Loc.T("Cmd.FpsUsage"));
            return;
        }

        Config.TargetFps = fps;
        Config.Save(PluginInterface);
        Print(Loc.T("Cmd.FpsSet", fps));
    }

    private void HandleBitrateCommand(string[] parts)
    {
        if (parts.Length == 1 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Print(Loc.T("Cmd.BitrateStatus", Config.VideoBitrate / 1_000_000));
            return;
        }

        if (!int.TryParse(parts[1], out int bitrateMbps) || bitrateMbps is < 1 or > 100)
        {
            Print(Loc.T("Cmd.BitrateUsage"));
            return;
        }

        Config.VideoBitrate = bitrateMbps * 1_000_000;
        Config.Save(PluginInterface);
        Print(Loc.T("Cmd.BitrateSet", bitrateMbps));
    }

    private void HandleAudioCommand(string[] parts)
    {
        if (parts.Length == 1 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Print(Loc.T("Cmd.AudioStatus", AudioModeText(Config.AudioCaptureMode)));
            return;
        }

        AudioCaptureMode? mode = ParseAudioMode(parts[1], Config.AudioCaptureMode);
        if (mode == null)
        {
            Print(Loc.T("Cmd.AudioUsage"));
            return;
        }

        Config.AudioCaptureMode = mode.Value;
        Config.CaptureAudio = mode.Value != AudioCaptureMode.Off;
        Config.Save(PluginInterface);
        Print(Loc.T("Cmd.AudioSet", AudioModeText(mode.Value)));
    }

    private void OpenOutputDirectory()
    {
        string dir = Config.GetEffectiveOutputDirectory(PluginInterface);
        try
        {
            ShellHelpers.OpenDirectory(dir);
            Print(Loc.T("Cmd.OutputOpened", dir));
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open output directory: {ex}");
            Print(Loc.T("Cmd.OutputFailed", ex.Message));
        }
    }

    private void PrintStatus()
    {
        string elapsed = RecordingService.Phase == RecordingPhase.Recording
            ? Loc.T("Cmd.StatusElapsed", RecordingService.Elapsed.ToString("hh\\:mm\\:ss"), RecordingService.FrameCount)
            : string.Empty;

        Print(Loc.T("Cmd.StatusLine", RecordingService.Phase.ToDisplayText(), elapsed));
        Print(Loc.T("Cmd.AutoRecordLine", Loc.OnOff(Config.AutoRecordEightPlayerDuty), AutoDutyRecordingService.StatusText));
        Print(Loc.T("Cmd.ParamsLine", Config.TargetFps, Config.VideoBitrate / 1_000_000, AudioModeText(Config.AudioCaptureMode), Loc.OnOff(Config.IncludeOverlay)));
    }

    private void HandleOverlayCommand(string[] parts)
    {
        HandleSwitchCommand(
            parts,
            Config.IncludeOverlay,
            Loc.T("Cmd.OverlayStatus", Loc.OnOff(Config.IncludeOverlay)),
            Loc.T("Cmd.OverlayUsage"),
            enabled => Config.IncludeOverlay = enabled,
            enabled => Loc.T(enabled ? "Cmd.OverlayOn" : "Cmd.OverlayOff"));
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

    private static string OnOff(bool enabled) => Loc.OnOff(enabled);

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
            AudioCaptureMode.Game => Loc.T("Config.AudioGame"),
            AudioCaptureMode.System => Loc.T("Config.AudioSystem"),
            _ => Loc.T("Config.AudioOff"),
        };
    }

    private static void Print(string message)
    {
        ChatGui.Print($"[Pocket Recorder] {message}");
    }

        private static void PrintHelp()
    {
        Print(Loc.T("Cmd.HelpLine1"));
        Print(Loc.T("Cmd.HelpLine2"));
        Print(Loc.T("Cmd.HelpLine3"));
        Print(Loc.T("Cmd.HelpLine4"));
        Print(Loc.T("Cmd.HelpLine5"));
        Print(Loc.T("Cmd.HelpLine6"));
    }

    private void OnUiBuilderDraw()
    {
        FloatingRecordWindow.SyncOpenState();
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
        WindowSystem.RemoveWindow(RecordingListWindow);
    }
}
