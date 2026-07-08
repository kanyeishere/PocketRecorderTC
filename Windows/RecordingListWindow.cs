using ImGuiNET;
using Dalamud.Interface.Windowing;
using Recorder.Localization;
using Recorder.Recording;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Recorder.Windows;

internal sealed class RecordingListWindow : Window
{
    private const string StarFilled = "★";
    private const string StarHollow = "☆";

    private readonly Plugin _plugin;
    private readonly List<RecordingFileItem> _items = [];
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private string _statusText = string.Empty;
    private string? _deleteConfirmPath;
    private bool _refreshRequested;
    private SortColumn _sortColumn = SortColumn.LastWrite;
    private bool _sortAscending;

    public RecordingListWindow(Plugin plugin) : base("###PocketRecorderRecordingList")
    {
        _plugin = plugin;
        Size = new Vector2(812, 440);
        SizeCondition = ImGuiCond.Appearing;
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public override void OnOpen()
    {
        Refresh();
    }

    public override void Draw()
    {
        WindowName = Loc.T("List.WindowTitle") + "###PocketRecorderRecordingList";

        if (_refreshRequested || (DateTime.UtcNow - _lastRefreshUtc) > TimeSpan.FromSeconds(5))
            Refresh();

        string outputDirectory = _plugin.Config.GetEffectiveOutputDirectory(Plugin.PluginInterface);
        DrawToolbar(outputDirectory);

        if (!string.IsNullOrWhiteSpace(_statusText))
            ImGui.TextWrapped(_statusText);

        ImGui.Separator();

        if (_items.Count == 0)
        {
            ImGui.TextDisabled(Loc.T("List.NoFiles"));
            return;
        }

        DrawTable();

        if (_refreshRequested)
            Refresh();
    }

    private void DrawToolbar(string outputDirectory)
    {
        if (ImGui.Button(Loc.T("List.Refresh")))
            Refresh();

        ImGui.SameLine();
        if (ImGui.Button(Loc.T("List.OpenOutputDir")))
            TryRun(() => ShellHelpers.OpenDirectory(outputDirectory));

        ImGui.SameLine();
        ImGui.TextDisabled(Loc.T("List.FileCount", _items.Count));

        ImGui.TextDisabled(outputDirectory);
    }

    private void DrawTable()
    {
        var tableFlags = ImGuiTableFlags.Borders |
                         ImGuiTableFlags.RowBg |
                         ImGuiTableFlags.Resizable |
                         ImGuiTableFlags.ScrollY;

        if (!ImGui.BeginTable("##PocketRecorderRecordingFiles", 6, tableFlags, new Vector2(0f, -1f)))
            return;

        ImGui.TableSetupColumn("★", ImGuiTableColumnFlags.WidthFixed, 34f);
        ImGui.TableSetupColumn(Loc.T("List.ColFile"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Loc.T("List.ColTime"), ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn(Loc.T("List.ColDuration"), ImGuiTableColumnFlags.WidthFixed, 78f);
        ImGui.TableSetupColumn(Loc.T("List.ColSize"), ImGuiTableColumnFlags.WidthFixed, 78f);
        ImGui.TableSetupColumn(Loc.T("List.ColActions"), ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupScrollFreeze(0, 1);
        DrawTableHeader();

        foreach (var item in _items)
            DrawRow(item);

        ImGui.EndTable();
    }

    private void DrawTableHeader()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawSortableHeaderCell("★", SortColumn.Starred);
        DrawSortableHeaderCell(Loc.T("List.ColFile"), SortColumn.FileName);
        DrawSortableHeaderCell(Loc.T("List.ColTime"), SortColumn.LastWrite);
        DrawSortableHeaderCell(Loc.T("List.ColDuration"), SortColumn.Duration);
        DrawSortableHeaderCell(Loc.T("List.ColSize"), SortColumn.Length);
        DrawHeaderCell(Loc.T("List.ColActions"));
    }

    private void DrawSortableHeaderCell(string text, SortColumn column)
    {
        ImGui.TableNextColumn();
        string arrow = _sortColumn == column
            ? _sortAscending ? " ↑" : " ↓"
            : string.Empty;

        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        if (ImGui.Selectable($"{text}{arrow}##Sort{column}", false, ImGuiSelectableFlags.None, new Vector2(width, 0f)))
        {
            if (_sortColumn == column)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = column;
                _sortAscending = column == SortColumn.FileName;
            }

            ApplySort();
        }
    }

    private static void DrawHeaderCell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private void DrawRow(RecordingFileItem item)
    {
        bool isCurrent = IsCurrentRecordingFile(item.FullPath);

        ImGui.PushID(item.FullPath);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawStarToggle(item, isCurrent);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(item.FileName);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(item.LastWriteLocal.ToString("MM-dd HH:mm:ss"));

        ImGui.TableNextColumn();
        TimeSpan? duration = isCurrent && _plugin.RecordingService.Phase == RecordingPhase.Recording
            ? _plugin.RecordingService.Elapsed
            : item.Duration;
        ImGui.TextUnformatted(FormatDuration(duration));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(FormatBytes(item.Length));

        ImGui.TableNextColumn();
        DrawActions(item, isCurrent);

        ImGui.PopID();
    }

    private void DrawStarToggle(RecordingFileItem item, bool disabled)
    {
        if (disabled)
            ImGui.BeginDisabled();

        if (ImGui.SmallButton(item.Starred ? StarFilled : StarHollow))
            ToggleFileStar(item);

        if (disabled)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            DrawTooltip(disabled ? Loc.T("List.CannotRenameRecording") : item.Starred ? Loc.T("List.StarRemove") : Loc.T("List.StarMark"));
    }

    private void DrawActions(RecordingFileItem item, bool disabled)
    {
        if (disabled)
            ImGui.BeginDisabled();

        if (ImGui.SmallButton(Loc.T("List.Play")))
            TryRun(() => ShellHelpers.OpenFile(item.FullPath));

        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("List.Locate")))
            TryRun(() => ShellHelpers.ShowFileInExplorer(item.FullPath));

        ImGui.SameLine();

        if (_deleteConfirmPath != null && string.Equals(_deleteConfirmPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            if (ImGui.SmallButton(Loc.T("List.Confirm")))
                DeleteFile(item.FullPath);

            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("List.Cancel")))
                _deleteConfirmPath = null;
        }
        else if (ImGui.SmallButton(Loc.T("List.Delete")))
        {
            _deleteConfirmPath = item.FullPath;
        }

        if (disabled)
            ImGui.EndDisabled();

        if (disabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            DrawTooltip(Loc.T("List.CannotOperateRecording"));
    }

    private void ToggleFileStar(RecordingFileItem item)
    {
        TryRun(() =>
        {
            RecordingFileNames.RenameStarred(item.FullPath, !item.Starred);
            _deleteConfirmPath = null;
            RequestRefresh();
        });
    }

    private void DeleteFile(string path)
    {
        TryRun(() =>
        {
            File.Delete(path);
            _deleteConfirmPath = null;
            RequestRefresh();
        });
    }

    private void Refresh()
    {
        _refreshRequested = false;
        _items.Clear();
        _lastRefreshUtc = DateTime.UtcNow;

        string outputDirectory = _plugin.Config.GetEffectiveOutputDirectory(Plugin.PluginInterface);
        if (!Directory.Exists(outputDirectory))
        {
            _statusText = Loc.T("List.OutputDirNotExist");
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(outputDirectory, "*.mp4", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                    continue;

                _items.Add(new RecordingFileItem(
                    info.FullName,
                    info.Name,
                    info.LastWriteTime,
                    info.Length,
                    MediaFileMetadata.TryReadMp4Duration(info.FullName),
                    RecordingFileNames.IsStarred(info.Name)));
            }

            ApplySort();
            _statusText = string.Empty;
        }
        catch (Exception ex)
        {
            _statusText = Loc.T("List.ReadListFailed", ex.Message);
            Plugin.Log.Warning($"[RecordingList] Refresh failed: {ex}");
        }
    }

    private void RequestRefresh()
    {
        _refreshRequested = true;
    }

    private void ApplySort()
    {
        _items.Sort((a, b) =>
        {
            int result = _sortColumn switch
            {
                SortColumn.Starred => a.Starred.CompareTo(b.Starred),
                SortColumn.FileName => string.Compare(a.FileName, b.FileName, StringComparison.CurrentCultureIgnoreCase),
                SortColumn.LastWrite => a.LastWriteLocal.CompareTo(b.LastWriteLocal),
                SortColumn.Duration => CompareNullableTimeSpan(a.Duration, b.Duration),
                SortColumn.Length => a.Length.CompareTo(b.Length),
                _ => 0,
            };

            return _sortAscending ? result : -result;
        });
    }

    private void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _statusText = ex.Message;
            Plugin.Log.Warning($"[RecordingList] Action failed: {ex}");
        }
    }

    private bool IsCurrentRecordingFile(string path)
    {
        if (_plugin.RecordingService.Phase == RecordingPhase.Idle)
            return false;

        string? current = _plugin.RecordingService.CurrentFilePath;
        if (string.IsNullOrWhiteSpace(current))
            return false;

        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(path, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void DrawTooltip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }

    private static int CompareNullableTimeSpan(TimeSpan? left, TimeSpan? right)
    {
        if (left.HasValue && right.HasValue)
            return left.Value.CompareTo(right.Value);

        if (left.HasValue)
            return -1;

        if (right.HasValue)
            return 1;

        return 0;
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration == null)
            return "--";

        TimeSpan value = duration.Value;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:0}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private sealed record RecordingFileItem(
        string FullPath,
        string FileName,
        DateTime LastWriteLocal,
        long Length,
        TimeSpan? Duration,
        bool Starred);

    private enum SortColumn
    {
        Starred,
        FileName,
        LastWrite,
        Duration,
        Length,
    }
}
