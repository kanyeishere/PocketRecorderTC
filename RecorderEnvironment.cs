using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Recorder;

internal interface IRecorderEnvironment
{
    IRecorderLogger Log { get; }
    IRecorderPaths Paths { get; }
}

internal interface IRecorderLogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}

internal interface IRecorderPaths
{
    string PluginConfigDirectory { get; }
}

internal sealed class DalamudRecorderEnvironment : IRecorderEnvironment, IRecorderPaths
{
    private readonly IDalamudPluginInterface _pluginInterface;

    public DalamudRecorderEnvironment(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        Log = new DalamudRecorderLogger(log);
    }

    public IRecorderLogger Log { get; }
    public IRecorderPaths Paths => this;
    public string PluginConfigDirectory => _pluginInterface.GetPluginConfigDirectory();

    private sealed class DalamudRecorderLogger : IRecorderLogger
    {
        private readonly IPluginLog _log;

        public DalamudRecorderLogger(IPluginLog log)
        {
            _log = log;
        }

        public void Info(string message) => _log.Info(message);
        public void Warning(string message) => _log.Warning(message);
        public void Error(string message) => _log.Error(message);
    }
}
