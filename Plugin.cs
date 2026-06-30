using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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

    internal Configuration Config { get; }
    internal RecordingService RecordingService { get; }
    internal ConfigWindow ConfigWindow { get; }

    internal Dalamud.Interface.Windowing.WindowSystem WindowSystem { get; }

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Config = Configuration.Load(pluginInterface);

        RecordingService = new RecordingService(this, GameInterop, Framework);
        ConfigWindow = new ConfigWindow(this);

        WindowSystem = new Dalamud.Interface.Windowing.WindowSystem("Recorder");
        WindowSystem.AddWindow(ConfigWindow);

        PluginInterface.UiBuilder.Draw += OnUiBuilderDraw;
        PluginInterface.UiBuilder.OpenConfigUi += () => ConfigWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开录制控制面板。再次输入则开始/停止录制。",
        });
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            RecordingService.ToggleRecording();
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
        RecordingService.Dispose();
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= OnUiBuilderDraw;
        WindowSystem.RemoveWindow(ConfigWindow);
    }
}
