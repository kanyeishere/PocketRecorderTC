using ImGuiNET;
using Dalamud.Interface.Windowing;
using Recorder.Encoding;
using Recorder.Recording;
using System;
using System.Numerics;
using System.Threading;

namespace Recorder.Windows;

internal sealed class ConfigWindow : Window
{
    private const string DiscordInviteUrl = "https://discord.gg/CQd4w7Bzv2";

    private readonly Plugin _plugin;
    private bool _ffmpegInstallInProgress;
    private string _ffmpegInstallStatus = string.Empty;

    public ConfigWindow(Plugin plugin) : base("Pocket Recorder###PocketRecorderConfig")
    {
        _plugin = plugin;
        Size = new Vector2(420, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public override void Draw()
    {
        var config = _plugin.Config;

        // ── 录制状态 ──
        DrawRecordingStatus();

        ImGui.Separator();

        DrawRecordingControls();

        DrawCommunityLink();

        ImGui.Separator();

        DrawAutomationSettings(config);

        ImGui.Separator();

        // ── 视频设置 ──
        DrawVideoSettings(config);

        ImGui.Separator();

        // ── 音频设置 ──
        DrawAudioSettings(config);

        ImGui.Separator();

        // ── 输出设置 ──
        DrawOutputSettings(config);

        ImGui.Separator();

        // ── FFmpeg 设置 ──
        DrawFFmpegSettings(config);

        ImGui.Separator();
        ImGui.TextDisabled("设置会自动保存");
    }

    private void DrawRecordingStatus()
    {
        var phase = _plugin.RecordingService.Phase;
        if (phase != RecordingPhase.Idle)
        {
            var elapsed = _plugin.RecordingService.Elapsed;
            int frameCount = _plugin.RecordingService.FrameCount;

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
            ImGui.Text($"{GetPhaseIndicator(phase)} {phase.ToDisplayText()}  {elapsed:hh\\:mm\\:ss}");
            ImGui.PopStyleColor();
            ImGui.Text($"帧数: {frameCount}");
            if (!string.IsNullOrWhiteSpace(_plugin.RecordingService.CurrentBackend))
                ImGui.Text($"后端: {_plugin.RecordingService.CurrentBackend}");
            if (!string.IsNullOrEmpty(_plugin.RecordingService.CurrentFilePath))
            {
                ImGui.TextWrapped($"文件: {_plugin.RecordingService.CurrentFilePath}");
            }
        }
        else
        {
            ImGui.Text("○ 待机中");
        }
    }

    private void DrawRecordingControls()
    {
        bool ffmpegBusy = _plugin.IsFFmpegBootstrapRunning && !_plugin.IsFFmpegBootstrapComplete;
        var phase = _plugin.RecordingService.Phase;
        if (ffmpegBusy)
        {
            ImGui.BeginDisabled();
            ImGui.Button("正在下载必要组件", new Vector2(-1, 32));
            ImGui.EndDisabled();
        }
        else if (phase == RecordingPhase.Recording || phase == RecordingPhase.Preparing)
        {
            if (ImGui.Button("■ 停止录制", new Vector2(-1, 32)))
            {
                _plugin.RecordingService.StopRecording();
            }
        }
        else if (phase == RecordingPhase.Finalizing)
        {
            ImGui.BeginDisabled();
            ImGui.Button("保存中", new Vector2(-1, 32));
            ImGui.EndDisabled();
        }
        else
        {
            if (ImGui.Button("● 开始录制", new Vector2(-1, 32)))
            {
                _plugin.RecordingService.StartRecording();
            }
        }
        ImGui.TextDisabled("快捷命令: /pktr start, /pktr end, /pktr status");
    }

    private static void DrawCommunityLink()
    {
        ImGui.Spacing();
        if (ImGui.Button("加入 Discord 社群", new Vector2(-1, 0)))
            OpenDiscordInvite();

        ImGui.TextDisabled(DiscordInviteUrl);
        ImGui.SameLine();
        if (ImGui.SmallButton("复制链接##discord"))
            ImGui.SetClipboardText(DiscordInviteUrl);
    }

    private void DrawAutomationSettings(Configuration config)
    {
        ImGui.Text("快捷与自动录制");

        bool floating = config.ShowFloatingRecordButton;
        if (ImGui.Checkbox("显示悬浮录制按钮", ref floating))
        {
            config.ShowFloatingRecordButton = floating;
            _plugin.FloatingRecordWindow.IsOpen = floating;
            SaveConfig(config);
        }

        bool autoDuty = config.AutoRecordEightPlayerDuty;
        if (ImGui.Checkbox("倒计时自动录制", ref autoDuty))
        {
            config.AutoRecordEightPlayerDuty = autoDuty;
            SaveConfig(config);
        }

        ImGui.TextDisabled($"自动录制状态: {_plugin.AutoDutyRecordingService.StatusText}");
    }

    private static string GetPhaseIndicator(RecordingPhase phase)
    {
        return phase switch
        {
            RecordingPhase.Finalizing => "◆",
            RecordingPhase.Idle => "○",
            _ => "●",
        };
    }

    private void DrawVideoSettings(Configuration config)
    {
        ImGui.Text("视频设置");

        string[] recordingBackends = { "显卡原生录制", "FFmpeg 录制" };
        RecordingBackendMode[] recordingBackendValues =
        [
            RecordingBackendMode.Native,
            RecordingBackendMode.FFmpeg,
        ];
        int recordingBackendIdx = Array.IndexOf(recordingBackendValues, config.RecordingBackendMode);
        if (recordingBackendIdx < 0)
            recordingBackendIdx = 0;

        if (ImGui.Combo("录制方式", ref recordingBackendIdx, recordingBackends, recordingBackends.Length))
        {
            config.RecordingBackendMode = recordingBackendValues[recordingBackendIdx];
            config.ForceFFmpegFallbackForTesting = false;
            SaveConfig(config);
        }

        ImGui.TextDisabled(config.RecordingBackendMode == RecordingBackendMode.Native
            ? "优先使用显卡原生录制；不可用时自动回退 FFmpeg。"
            : "固定使用 FFmpeg 录制。");

        int bitrate = config.VideoBitrate / 1_000_000;
        if (ImGui.SliderInt("码率 (Mbps)", ref bitrate, 1, 100))
        {
            config.VideoBitrate = bitrate * 1_000_000;
        }
        SaveConfigAfterItemEdit(config);

        int fps = config.TargetFps;
        if (ImGui.SliderInt("目标帧率", ref fps, 15, 144))
        {
            config.TargetFps = fps;
        }
        SaveConfigAfterItemEdit(config);

        string[] outputScales = { "原始尺寸", "1/4 输出" };
        VideoOutputScaleMode[] outputScaleValues =
        [
            VideoOutputScaleMode.Original,
            VideoOutputScaleMode.QuarterPixels,
        ];
        int outputScaleIdx = Array.IndexOf(outputScaleValues, config.VideoOutputScaleMode);
        if (outputScaleIdx < 0)
            outputScaleIdx = 0;

        if (ImGui.Combo("输出分辨率", ref outputScaleIdx, outputScales, outputScales.Length))
        {
            config.VideoOutputScaleMode = outputScaleValues[outputScaleIdx];
            SaveConfig(config);
        }
        ImGui.TextDisabled("1/4 输出会保持画面比例，宽高各缩到 1/2。");

        bool includeOverlay = config.IncludeOverlay;
        if (ImGui.Checkbox("录制卫月界面", ref includeOverlay))
        {
            config.IncludeOverlay = includeOverlay;
            SaveConfig(config);
        }
        ImGui.TextDisabled("开启后会包含 Dalamud/ImGui 叠加层，下次录制生效。");

        string[] modes = { "自动", "兼容", "高级" };
        int modeIdx = config.VideoCodec == "auto"
            ? (config.UseHardwareEncoder ? 0 : 1)
            : 2;
        if (ImGui.Combo("编码模式", ref modeIdx, modes, modes.Length))
        {
            if (modeIdx == 0)
            {
                config.VideoCodec = "auto";
                config.UseHardwareEncoder = true;
                config.EncoderPreset = "auto";
                SaveConfig(config);
            }
            else if (modeIdx == 1)
            {
                config.VideoCodec = "auto";
                config.UseHardwareEncoder = false;
                config.EncoderPreset = "auto";
                SaveConfig(config);
            }
            else if (config.VideoCodec == "auto")
            {
                string resolvedFFmpeg = FFmpegBootstrapper.ResolveOrInstall(
                    config.FFmpegPath,
                    Plugin.PluginInterface.GetPluginConfigDirectory());
                var encoder = FFmpegEncoderSelector.Select(resolvedFFmpeg, config);
                config.VideoCodec = encoder.Codec;
                config.EncoderPreset = "auto";
                config.UseHardwareEncoder = true;
                SaveConfig(config);
            }
        }

    }

    private void DrawAudioSettings(Configuration config)
    {
        ImGui.Text("音频设置");
        string[] audioModes = { "不录制声音", "只录制游戏声音", "录制系统声音" };
        AudioCaptureMode[] modeValues = { AudioCaptureMode.Off, AudioCaptureMode.Game, AudioCaptureMode.System };
        int modeIdx = Array.IndexOf(modeValues, config.AudioCaptureMode);
        if (modeIdx < 0) modeIdx = 1;

        if (ImGui.Combo("声音来源", ref modeIdx, audioModes, audioModes.Length))
        {
            config.AudioCaptureMode = modeValues[modeIdx];
            config.CaptureAudio = config.AudioCaptureMode != AudioCaptureMode.Off;
            SaveConfig(config);
        }

        ImGui.TextDisabled(GetAudioModeDescription(config.AudioCaptureMode));
    }

    private void DrawFFmpegSettings(Configuration config)
    {
        ImGui.Text("FFmpeg 设置");

        // FFmpeg 路径
        string ffmpegPath = config.FFmpegPath;
        if (ImGui.InputText("FFmpeg 路径 (空=自动)", ref ffmpegPath, 512))
        {
            config.FFmpegPath = ffmpegPath;
        }
        SaveConfigAfterItemEdit(config);
        ImGui.TextDisabled($"当前: {config.GetEffectiveFFmpegPath(Plugin.PluginInterface)}");
        DrawBundledFFmpegControls(config);

#if DEBUG
        bool forceFFmpeg = config.ForceFFmpegFallbackForTesting;
        if (ImGui.Checkbox("本地测试 FFmpeg fallback", ref forceFFmpeg))
        {
            config.ForceFFmpegFallbackForTesting = forceFFmpeg;
            SaveConfig(config);
        }

        if (ImGui.Button("本地测试 NVIDIA 驱动提醒", new Vector2(-1, 0)))
            _plugin.RecordingService.ShowNvencDriverUpdateToastForTesting();
#endif

        ImGui.TextDisabled($"编码模式: {GetEncodingModeText(config)}");

        if (config.VideoCodec != "auto")
        {
            string[] codecs = { "hevc_nvenc", "hevc_qsv", "hevc_amf", "h264_nvenc", "h264_qsv", "h264_amf", "libx264", "libx265" };
            int codecIdx = Array.IndexOf(codecs, config.VideoCodec);
            if (codecIdx < 0) codecIdx = 0;
            if (ImGui.Combo("视频编码器", ref codecIdx, codecs, codecs.Length))
            {
                config.VideoCodec = codecs[codecIdx];
                SaveConfig(config);
            }

            string preset = config.EncoderPreset;
            if (ImGui.InputText("预设 (auto=推荐)", ref preset, 64))
            {
                config.EncoderPreset = preset;
            }
            SaveConfigAfterItemEdit(config);
            ImGui.TextDisabled($"实际预设: {config.ResolvePreset()}");
        }
    }

    private void DrawBundledFFmpegControls(Configuration config)
    {
        bool bootstrapBusy = _plugin.IsFFmpegBootstrapRunning && !_plugin.IsFFmpegBootstrapComplete;
        bool busy = _ffmpegInstallInProgress || bootstrapBusy;

        if (busy)
        {
            ImGui.BeginDisabled();
            ImGui.Button("下载中", new Vector2(-1, 0));
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("下载/更新内置 FFmpeg", new Vector2(-1, 0)))
        {
            StartBundledFFmpegInstall(config);
        }

        string status = _ffmpegInstallStatus;
        if (string.IsNullOrWhiteSpace(status))
            status = _plugin.FFmpegBootstrapStatus;

        if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextDisabled(status);
    }

    private void StartBundledFFmpegInstall(Configuration config)
    {
        _ffmpegInstallInProgress = true;
        _ffmpegInstallStatus = "正在下载 FFmpeg...";

        var thread = new Thread(() =>
        {
            try
            {
                string installedPath = FFmpegBootstrapper.InstallOrUpdateBundled(
                    Plugin.PluginInterface.GetPluginConfigDirectory());
                config.FFmpegPath = installedPath;
                config.Save(Plugin.PluginInterface);
                _ffmpegInstallStatus = $"已安装: {installedPath}";
            }
            catch (Exception ex)
            {
                Plugin.Log!.Error($"[FFmpeg] Manual install/update failed: {ex}");
                _ffmpegInstallStatus = $"下载失败: {ex.Message}";
            }
            finally
            {
                _ffmpegInstallInProgress = false;
            }
        })
        {
            IsBackground = true,
            Name = "Recorder-FFmpegInstall",
        };
        thread.Start();
    }

    private static string GetEncodingModeText(Configuration config)
    {
        if (config.ForceFFmpegRecording)
            return config.EffectiveForceFFmpegFallbackForTesting ? "本地测试 FFmpeg 录制" : "FFmpeg 录制";

        if (config.VideoCodec != "auto")
            return $"高级 ({config.VideoCodec})";

        return config.UseHardwareEncoder
            ? "自动选择硬件编码器，失败时回退兼容模式"
            : "兼容模式";
    }

    private static string GetAudioModeDescription(AudioCaptureMode mode)
    {
        return mode switch
        {
            AudioCaptureMode.Game => "只录制 FFXIV 进程及其子进程的声音。",
            AudioCaptureMode.System => "录制默认播放设备上的全部系统声音。",
            _ => "视频将不包含音轨。",
        };
    }

    private void DrawOutputSettings(Configuration config)
    {
        ImGui.Text("输出设置");

        string dir = config.OutputDirectory;
        if (ImGui.InputText("输出目录 (空=默认)", ref dir, 256))
        {
            config.OutputDirectory = dir;
        }
        SaveConfigAfterItemEdit(config);

        string effectiveDir = config.GetEffectiveOutputDirectory(Plugin.PluginInterface);
        ImGui.TextDisabled($"当前: {effectiveDir}");

        if (ImGui.Button("打开输出目录"))
        {
            try
            {
                Recorder.ShellHelpers.OpenDirectory(effectiveDir);
            }
            catch (Exception ex)
            {
                Plugin.Log!.Error($"Failed to open directory: {ex}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("录像列表"))
            _plugin.RecordingListWindow.IsOpen = true;

        int retentionDays = config.RecordingRetentionDays;
        ImGui.InputInt("录像保存期限 (天，0=永久)", ref retentionDays);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.RecordingRetentionDays = Math.Clamp(retentionDays, 0, RecordingRetentionCleanupService.MaxRetentionDays);
            SaveConfig(config);
        }

        if (config.RecordingRetentionDays <= 0)
        {
            ImGui.TextDisabled("自动清理关闭，不会删除任何录像。");
        }
        else
        {
            ImGui.TextDisabled($"仅清理输出目录中早于 {config.RecordingRetentionDays} 天的 Pocket Recorder 录像。");
            ImGui.TextDisabled("清理会永久删除文件；录制中会自动跳过。");
        }

        string cleanupStatus = _plugin.RetentionCleanupService.LastStatusText;
        if (!string.IsNullOrWhiteSpace(cleanupStatus))
            ImGui.TextDisabled(cleanupStatus);
    }

    private static void SaveConfig(Configuration config)
    {
        config.Save(Plugin.PluginInterface);
    }

    private static void SaveConfigAfterItemEdit(Configuration config)
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(config);
    }

    private static void OpenDiscordInvite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DiscordInviteUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Plugin.Log!.Error($"Failed to open Discord invite: {ex}");
        }
    }
}
