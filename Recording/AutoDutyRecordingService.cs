using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Recorder.Recording;

internal sealed class AutoDutyRecordingService : IDisposable
{
    private static readonly Regex InvalidFileNameCharsRegex = new(
        $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private readonly Plugin _plugin;
    private readonly IClientState _clientState;
    private readonly IDutyState _dutyState;
    private readonly IFramework _framework;
    private readonly object _sync = new();

    private bool _wasCountdownActive;
    private bool _autoRecordingActive;
    private DateTime _recordStartTime;
    private DateTime _recordEndTime;
    private string _recordDutyName = string.Empty;
    private string? _pendingTemporaryPath;

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
                    return $"自动录制中: {_recordDutyName}";

                return "等待倒计时";
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

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_plugin.Config.AutoRecordEightPlayerDuty)
            return;

        CountdownState countdown = GetCountdownState();
        bool countdownStarted = countdown.Active && !_wasCountdownActive;
        _wasCountdownActive = countdown.Active;

        if (!countdownStarted)
            return;

        TryStartAutoRecording(countdown.RemainingSeconds);
    }

    private bool TryStartAutoRecording(float countdownRemainingSeconds)
    {
        lock (_sync)
        {
            if (_autoRecordingActive || _plugin.RecordingService.Phase != RecordingPhase.Idle)
                return false;

            _recordStartTime = DateTime.Now;
            _recordEndTime = DateTime.MinValue;
            _recordDutyName = GetDutyName();
            _pendingTemporaryPath = CreateTemporaryOutputPath(_recordDutyName, _recordStartTime);

            if (!_plugin.RecordingService.StartRecording(_pendingTemporaryPath, OnRecordingFinished))
            {
                _pendingTemporaryPath = null;
                return false;
            }

            _autoRecordingActive = true;
            Plugin.Log.Info($"[AutoDuty] Started from countdown agent: duty={_recordDutyName}, remaining={countdownRemainingSeconds:0.0}s, path={_pendingTemporaryPath}");
            return true;
        }
    }

    private void OnDutyWiped(object? sender, ushort territoryType)
    {
        Plugin.Log.Info("[AutoDuty] Duty wipe detected.");
        StopAutoRecording("wipe");
    }

    private void OnTerritoryChanged(ushort territoryType)
    {
        _wasCountdownActive = false;
        StopAutoRecording("territory changed");
    }

    private void OnLogout(int type, int code)
    {
        _wasCountdownActive = false;
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

    private static unsafe CountdownState GetCountdownState()
    {
        try
        {
            var framework = Framework.Instance();
            if (framework == null)
                return default;

            var uiModule = framework->GetUIModule();
            if (uiModule == null)
                return default;

            var agentModule = uiModule->GetAgentModule();
            if (agentModule == null)
                return default;

            var countdownAgent = agentModule->GetAgentByInternalId(AgentId.CountDownSettingDialog);
            if (countdownAgent == null)
                return default;

            float timer = *(float*)((byte*)countdownAgent + 0x28);
            bool isActive = *(bool*)((byte*)countdownAgent + 0x38);
            return isActive && timer > 0f
                ? new CountdownState(true, timer)
                : default;
        }
        catch
        {
            return default;
        }
    }

    private string GetDutyName()
    {
        string name = GetContentFinderConditionName();
        if (string.IsNullOrWhiteSpace(name))
            name = GetTerritoryPlaceName(_clientState.TerritoryType);

        return SanitizeFileName(name);
    }

    private static unsafe string GetContentFinderConditionName()
    {
        try
        {
            var rowId = GameMain.Instance()->CurrentContentFinderConditionId;
            var row = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(rowId);
            return row?.Name.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to read current content finder condition name");
            return string.Empty;
        }
    }

    private static string GetTerritoryPlaceName(uint territoryType)
    {
        try
        {
            var row = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryType);
            return row?.PlaceName.ValueNullable?.Name.ToString() ?? "Duty";
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to read current territory place name");
            return "Duty";
        }
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
        string sanitized = InvalidFileNameCharsRegex.Replace(value.Trim(), "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "Duty" : sanitized;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        _dutyState.DutyWiped -= OnDutyWiped;
    }

    private readonly record struct CountdownState(bool Active, float RemainingSeconds);
}
