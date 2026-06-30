using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using OmenTools.Info.Game.Data;
using OmenTools.OmenService;
using Recorder.Recording;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Recorder.Recording;

internal sealed class AutoDutyRecordingService : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IClientState _clientState;
    private readonly IDutyState _dutyState;
    private readonly IFramework _framework;
    private readonly object _sync = new();

    private bool _wasCountdownVisible;
    private bool _autoRecordingActive;
    private DateTime _recordStartTime;
    private DateTime _recordEndTime;
    private string _recordDutyName = string.Empty;
    private string? _pendingTemporaryPath;
    private bool _testScenarioActive;

    public AutoDutyRecordingService(Plugin plugin, IClientState clientState, IDutyState dutyState, IFramework framework)
    {
        _plugin = plugin;
        _clientState = clientState;
        _dutyState = dutyState;
        _framework = framework;

        _framework.Update += OnFrameworkUpdate;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
        _dutyState.DutyWiped += OnDutyWiped;
    }

    public string StatusText
    {
        get
        {
            lock (_sync)
            {
                if (_autoRecordingActive)
                    return _testScenarioActive
                        ? $"自动录制测试中: {_recordDutyName}"
                        : $"自动录制中: {_recordDutyName}";

                if (IsEightPlayerDuty())
                    return "等待倒计时";

                return "等待 8 人副本";
            }
        }
    }

    public bool IsAutoRecordingActive
    {
        get
        {
            lock (_sync)
                return _autoRecordingActive;
        }
    }

    public bool IsTestScenarioActive
    {
        get
        {
            lock (_sync)
                return _testScenarioActive;
        }
    }

    public bool StartTestCountdown()
    {
        bool started = TryStartAutoRecording("自动录制测试", true);
        if (started)
            Plugin.Log.Info("[AutoDuty] Test countdown started.");

        return started;
    }

    public void StopTestAsWipe()
    {
        Plugin.Log.Info("[AutoDuty] Test wipe requested.");
        StopAutoRecording("test wipe");
    }

    public void StopTestAsLeave()
    {
        Plugin.Log.Info("[AutoDuty] Test leave requested.");
        StopAutoRecording("test leave");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_plugin.Config.AutoRecordEightPlayerDuty)
            return;

        bool countdownVisible = IsCountdownVisible();
        bool countdownStarted = countdownVisible && !_wasCountdownVisible;
        _wasCountdownVisible = countdownVisible;

        if (!countdownStarted || !IsEightPlayerDuty())
            return;

        TryStartAutoRecording(null, false);
    }

    private bool TryStartAutoRecording(string? forcedDutyName, bool isTestScenario)
    {
        lock (_sync)
        {
            if (_autoRecordingActive || _plugin.RecordingService.Phase != RecordingPhase.Idle)
                return false;

            _recordStartTime = DateTime.Now;
            _recordEndTime = DateTime.MinValue;
            _recordDutyName = SanitizeFileName(string.IsNullOrWhiteSpace(forcedDutyName) ? GetDutyName() : forcedDutyName);
            _pendingTemporaryPath = CreateTemporaryOutputPath(_recordDutyName, _recordStartTime);
            _testScenarioActive = isTestScenario;

            if (!_plugin.RecordingService.StartRecording(_pendingTemporaryPath, OnRecordingFinished))
            {
                _pendingTemporaryPath = null;
                _testScenarioActive = false;
                return false;
            }

            _autoRecordingActive = true;
            Plugin.Log.Info($"[AutoDuty] Started: duty={_recordDutyName}, path={_pendingTemporaryPath}");
            return true;
        }
    }

    private void OnDutyWiped(IDutyStateEventArgs args)
    {
        Plugin.Log.Info("[AutoDuty] Duty wipe detected.");
        StopAutoRecording("wipe");
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        _wasCountdownVisible = false;

        if (!IsEightPlayerDuty())
            StopAutoRecording("territory changed");
    }

    private void OnLogout(int type, int code)
    {
        _wasCountdownVisible = false;
        StopAutoRecording("logout");
    }

    private void StopAutoRecording(string reason)
    {
        lock (_sync)
        {
            if (!_autoRecordingActive)
                return;

            Plugin.Log.Info($"[AutoDuty] Stopping: {reason}");
            _recordEndTime = DateTime.Now;
            _plugin.RecordingService.StopRecording();
        }
    }

    private void OnRecordingFinished(RecordingFinishedEventArgs args)
    {
        string dutyName;
        DateTime startTime;
        DateTime endTime;

        lock (_sync)
        {
            if (!_autoRecordingActive)
                return;

            dutyName = _recordDutyName;
            startTime = _recordStartTime;
            endTime = _recordEndTime == DateTime.MinValue ? DateTime.Now : _recordEndTime;
            _autoRecordingActive = false;
            _recordDutyName = string.Empty;
            _recordEndTime = DateTime.MinValue;
            _pendingTemporaryPath = null;
            _testScenarioActive = false;
        }

        if (!args.Saved || !File.Exists(args.OutputPath))
        {
            Plugin.Log.Warning($"[AutoDuty] Recording did not produce a file: {args.OutputPath}");
            return;
        }

        string finalPath = CreateFinalOutputPath(args.OutputPath, dutyName, startTime, endTime);
        try
        {
            if (!string.Equals(args.OutputPath, finalPath, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(finalPath))
                    finalPath = AppendCollisionSuffix(finalPath);

                File.Move(args.OutputPath, finalPath);
            }

            Plugin.Log.Info($"[AutoDuty] Saved: {finalPath}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[AutoDuty] Rename failed: {ex.Message}");
        }
    }

    private static unsafe bool IsCountdownVisible()
    {
        var addon = Addons.ScreenInfoCountDown;
        return addon != null && addon->IsVisible && addon->IsFullyLoaded();
    }

    private static bool IsEightPlayerDuty()
    {
        if (!GameState.IsInInstanceArea || GameState.ContentFinderCondition == 0)
            return false;

        var content = GameState.ContentFinderConditionData;
        if (content.RowId == 0)
            return false;

        var memberType = content.ContentMemberType.ValueNullable;
        if (memberType == null)
            return false;

        int members = memberType.Value.TanksPerParty +
                      memberType.Value.HealersPerParty +
                      memberType.Value.MeleesPerParty +
                      memberType.Value.RangedPerParty;
        int parties = Math.Max(1, (int)memberType.Value.PartyCount);

        return members * parties == 8;
    }

    private static string GetDutyName()
    {
        string name = GameState.ContentFinderConditionData.Name.ToString();
        if (string.IsNullOrWhiteSpace(name))
            name = GameState.TerritoryTypeData.PlaceName.ValueNullable?.Name.ToString() ?? "Duty";

        return SanitizeFileName(name);
    }

    private string CreateTemporaryOutputPath(string dutyName, DateTime startTime)
    {
        string dir = _plugin.Config.GetEffectiveOutputDirectory(Plugin.PluginInterface);
        string fileName = $"{dutyName}-{startTime:yyyyMMdd-HHmmss}-recording.mp4";
        return Path.Combine(dir, fileName);
    }

    private static string CreateFinalOutputPath(string temporaryPath, string dutyName, DateTime startTime, DateTime endTime)
    {
        string directory = Path.GetDirectoryName(temporaryPath) ?? string.Empty;
        string fileName = $"{SanitizeFileName(dutyName)}-{startTime:yyyyMMdd}-{startTime:HHmmss}-{endTime:HHmmss}.mp4";
        return Path.Combine(directory, fileName);
    }

    private static string AppendCollisionSuffix(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{name}-{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{ext}");
    }

    private static string SanitizeFileName(string value)
    {
        string invalid = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        string sanitized = Regex.Replace(value.Trim(), $"[{invalid}]+", "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "Duty" : sanitized;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        _dutyState.DutyWiped -= OnDutyWiped;
    }
}
