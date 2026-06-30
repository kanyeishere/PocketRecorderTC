using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OmenTools;
using Recorder.Recording;
using Recorder.Windows;

namespace Recorder;

public sealed class Plugin : IDalamudPlugin
{
    internal const string CommandName = "/record";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;

    internal Configuration Config { get; }
    internal RecordingService RecordingService { get; }
    internal AutoDutyRecordingService AutoDutyRecordingService { get; }
    internal ConfigWindow ConfigWindow { get; }
    internal FloatingRecordWindow FloatingRecordWindow { get; }

    internal Dalamud.Interface.Windowing.WindowSystem WindowSystem { get; }

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DService.Init(pluginInterface);
        Config = Configuration.Load(pluginInterface);

        RecordingService = new RecordingService(this, GameInterop, Framework);
        AutoDutyRecordingService = new AutoDutyRecordingService(this, ClientState, DutyState, Framework);
        ConfigWindow = new ConfigWindow(this);
        FloatingRecordWindow = new FloatingRecordWindow(this);

        WindowSystem = new Dalamud.Interface.Windowing.WindowSystem("Pocket Recorder");
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(FloatingRecordWindow);

        PluginInterface.UiBuilder.Draw += OnUiBuilderDraw;
        PluginInterface.UiBuilder.OpenConfigUi += () => ConfigWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开录制控制面板。参数: toggle, test start, test wipe, test leave。",
        });
    }

    private void OnCommand(string command, string args)
    {
        string trimmed = args.Trim();
        if (trimmed.Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            RecordingService.ToggleRecording();
        }
        else if (trimmed.Equals("test start", StringComparison.OrdinalIgnoreCase))
        {
            AutoDutyRecordingService.StartTestCountdown();
        }
        else if (trimmed.Equals("test wipe", StringComparison.OrdinalIgnoreCase))
        {
            AutoDutyRecordingService.StopTestAsWipe();
        }
        else if (trimmed.Equals("test leave", StringComparison.OrdinalIgnoreCase))
        {
            AutoDutyRecordingService.StopTestAsLeave();
        }
        else
        {
            ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
        }
    }

    private void OnUiBuilderDraw()
    {
        WindowSystem.Draw();
    }

    public void Dispose()
    {
        AutoDutyRecordingService.Dispose();
        RecordingService.Dispose();
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= OnUiBuilderDraw;
        WindowSystem.RemoveWindow(FloatingRecordWindow);
        WindowSystem.RemoveWindow(ConfigWindow);
        DService.Uninit();
    }
}
