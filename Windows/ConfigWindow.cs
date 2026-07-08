using ImGuiNET;
using Dalamud.Interface.Windowing;
using Recorder.Encoding;
using Recorder.Localization;
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

    public ConfigWindow(Plugin plugin) : base("###PocketRecorderConfig")
    {
        _plugin = plugin;
        Size = new Vector2(420, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public override void Draw()
    {
        WindowName = Loc.T("Config.WindowTitle") + "###PocketRecorderConfig";

        var config = _plugin.Config;

        // ── 录制状态 ──
        DrawRecordingStatus();

        ImGui.Separator();

        DrawRecordingControls();

        DrawCommunityLink();

        ImGui.Separator();

        DrawGeneralSettings(config);

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

        // ── 隐私与诊断 ──
        DrawDiagnosticsSettings(config);

        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("Config.SettingsAutoSave"));
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
            ImGui.Text(Loc.T("Config.FrameCount", frameCount));
            if (!string.IsNullOrWhiteSpace(_plugin.RecordingService.CurrentBackend))
                ImGui.Text(Loc.T("Config.Backend", _plugin.RecordingService.CurrentBackend));
            if (!string.IsNullOrEmpty(_plugin.RecordingService.CurrentFilePath))
            {
                ImGui.TextWrapped(Loc.T("Config.File", _plugin.RecordingService.CurrentFilePath));
            }
        }
        else
        {
            ImGui.Text(Loc.T("Config.Idle"));
        }
    }

    private void DrawRecordingControls()
    {
        bool ffmpegBusy = _plugin.IsFFmpegBootstrapRunning && !_plugin.IsFFmpegBootstrapComplete;
        var phase = _plugin.RecordingService.Phase;
        if (ffmpegBusy)
        {
            ImGui.BeginDisabled();
            ImGui.Button(Loc.T("Config.DownloadingComponents"), new Vector2(-1, 32));
            ImGui.EndDisabled();
        }
        else if (phase == RecordingPhase.Recording || phase == RecordingPhase.Preparing)
        {
            if (ImGui.Button(Loc.T("Config.StopRecording"), new Vector2(-1, 32)))
            {
                _plugin.RecordingService.StopRecording();
            }
        }
        else if (phase == RecordingPhase.Finalizing)
        {
            ImGui.BeginDisabled();
            ImGui.Button(Loc.T("Config.Saving"), new Vector2(-1, 32));
            ImGui.EndDisabled();
        }
        else
        {
            if (ImGui.Button(Loc.T("Config.StartRecording"), new Vector2(-1, 32)))
            {
                _plugin.RecordingService.StartRecording();
            }
        }
        ImGui.TextDisabled(Loc.T("Config.QuickCommands"));
    }

    private static void DrawCommunityLink()
    {
        ImGui.Spacing();
        if (ImGui.Button(Loc.T("Config.JoinDiscord"), new Vector2(-1, 0)))
            OpenDiscordInvite();

        ImGui.TextDisabled(DiscordInviteUrl);
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("Config.CopyLink") + "##discord"))
            ImGui.SetClipboardText(DiscordInviteUrl);
    }

    private void DrawGeneralSettings(Configuration config)
    {
        ImGui.Text(Loc.T("Config.GeneralSettings"));

        // Language selector
        string[] langLabels =
        {
            Loc.T("Config.LanguageAuto"),
            Loc.T("Config.LanguageEnglish"),
            Loc.T("Config.LanguageJapanese"),
            Loc.T("Config.LanguageChineseSimplified"),
            Loc.T("Config.LanguageChineseTraditional"),
        };
        AppLanguage[] langValues =
        {
            AppLanguage.Auto,
            AppLanguage.English,
            AppLanguage.Japanese,
            AppLanguage.ChineseSimplified,
            AppLanguage.ChineseTraditional,
        };
        int langIdx = Array.IndexOf(langValues, config.Language);
        if (langIdx < 0)
            langIdx = 0;

        if (ImGui.Combo(Loc.T("Config.Language"), ref langIdx, langLabels, langLabels.Length))
        {
            config.Language = langValues[langIdx];
            SaveConfig(config);
            Loc.Initialize(config.Language, Plugin.PluginInterface.UiLanguage);
        }

        ImGui.Separator();

        ImGui.Text(Loc.T("Config.ShortcutsAndAuto"));

        bool floating = config.ShowFloatingRecordButton;
        if (ImGui.Checkbox(Loc.T("Config.ShowFloatingButton"), ref floating))
        {
            config.ShowFloatingRecordButton = floating;
            _plugin.FloatingRecordWindow.IsOpen = floating;
            SaveConfig(config);
        }

        if (floating)
        {
            ImGui.Indent();
            float scale = config.FloatingRecordButtonScale;
            if (ImGui.SliderFloat(Loc.T("Config.FloatingButtonScale"), ref scale, 0.5f, 2.0f, "%.1fx"))
            {
                config.FloatingRecordButtonScale = Math.Clamp(scale, 0.5f, 2.0f);
                SaveConfig(config);
            }

            if (ImGui.Button(Loc.T("Config.ResetFloatingPosition")))
            {
                config.FloatingRecordButtonPosition = new Vector2(48f, 180f);
                config.HasFloatingRecordButtonPosition = true;
                _plugin.FloatingRecordWindow.Position = config.FloatingRecordButtonPosition;
                _plugin.FloatingRecordWindow.PositionCondition = ImGuiCond.Always;
                SaveConfig(config);
            }
            ImGui.Unindent();
        }

        bool autoDuty = config.AutoRecordEightPlayerDuty;
        if (ImGui.Checkbox(Loc.T("Config.CountdownAutoRecord"), ref autoDuty))
        {
            config.AutoRecordEightPlayerDuty = autoDuty;
            SaveConfig(config);
        }

        ImGui.TextDisabled(Loc.T("Config.AutoRecordStatus", _plugin.AutoDutyRecordingService.StatusText));
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
        ImGui.Text(Loc.T("Config.VideoSettings"));

        string[] recordingBackends = { Loc.T("Config.NativeRecording"), Loc.T("Config.FFmpegRecording") };
        RecordingBackendMode[] recordingBackendValues =
        [
            RecordingBackendMode.Native,
            RecordingBackendMode.FFmpeg,
        ];
        int recordingBackendIdx = Array.IndexOf(recordingBackendValues, config.RecordingBackendMode);
        if (recordingBackendIdx < 0)
            recordingBackendIdx = 0;

        if (ImGui.Combo(Loc.T("Config.RecordingMethod"), ref recordingBackendIdx, recordingBackends, recordingBackends.Length))
        {
            config.RecordingBackendMode = recordingBackendValues[recordingBackendIdx];
            config.ForceFFmpegFallbackForTesting = false;
            SaveConfig(config);
        }

        ImGui.TextDisabled(config.RecordingBackendMode == RecordingBackendMode.Native
            ? Loc.T("Config.NativeRecordingDesc")
            : Loc.T("Config.FFmpegRecordingDesc"));

        int bitrate = config.VideoBitrate / 1_000_000;
        if (ImGui.SliderInt(Loc.T("Config.Bitrate"), ref bitrate, 1, 100))
        {
            config.VideoBitrate = bitrate * 1_000_000;
        }
        SaveConfigAfterItemEdit(config);

        int fps = config.TargetFps;
        if (ImGui.SliderInt(Loc.T("Config.TargetFps"), ref fps, 15, 144))
        {
            config.TargetFps = fps;
        }
        SaveConfigAfterItemEdit(config);

        string[] outputScales = { Loc.T("Config.OriginalSize"), Loc.T("Config.QuarterOutput") };
        VideoOutputScaleMode[] outputScaleValues =
        [
            VideoOutputScaleMode.Original,
            VideoOutputScaleMode.QuarterPixels,
        ];
        int outputScaleIdx = Array.IndexOf(outputScaleValues, config.VideoOutputScaleMode);
        if (outputScaleIdx < 0)
            outputScaleIdx = 0;

        if (ImGui.Combo(Loc.T("Config.OutputResolution"), ref outputScaleIdx, outputScales, outputScales.Length))
        {
            config.VideoOutputScaleMode = outputScaleValues[outputScaleIdx];
            SaveConfig(config);
        }
        ImGui.TextDisabled(Loc.T("Config.QuarterOutputDesc"));

        bool includeOverlay = config.IncludeOverlay;
        if (ImGui.Checkbox(Loc.T("Config.RecordOverlay"), ref includeOverlay))
        {
            config.IncludeOverlay = includeOverlay;
            SaveConfig(config);
        }
        ImGui.TextDisabled(Loc.T("Config.OverlayDesc"));

        string[] modes = { Loc.T("Config.EncodingAuto"), Loc.T("Config.EncodingCompatible"), Loc.T("Config.EncodingAdvanced") };
        int modeIdx = config.VideoCodec == "auto"
            ? (config.UseHardwareEncoder ? 0 : 1)
            : 2;
        if (ImGui.Combo(Loc.T("Config.EncodingMode"), ref modeIdx, modes, modes.Length))
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
        ImGui.Text(Loc.T("Config.AudioSettings"));
        string[] audioModes = { Loc.T("Config.AudioOff"), Loc.T("Config.AudioGame"), Loc.T("Config.AudioSystem") };
        AudioCaptureMode[] modeValues = { AudioCaptureMode.Off, AudioCaptureMode.Game, AudioCaptureMode.System };
        int modeIdx = Array.IndexOf(modeValues, config.AudioCaptureMode);
        if (modeIdx < 0) modeIdx = 1;

        if (ImGui.Combo(Loc.T("Config.AudioSource"), ref modeIdx, audioModes, audioModes.Length))
        {
            config.AudioCaptureMode = modeValues[modeIdx];
            config.CaptureAudio = config.AudioCaptureMode != AudioCaptureMode.Off;
            SaveConfig(config);
        }

        ImGui.TextDisabled(GetAudioModeDescription(config.AudioCaptureMode));
    }

    private void DrawFFmpegSettings(Configuration config)
    {
        ImGui.Text(Loc.T("Config.FFmpegSettings"));

        // FFmpeg 路径
        string ffmpegPath = config.FFmpegPath;
        if (ImGui.InputText(Loc.T("Config.FFmpegPath"), ref ffmpegPath, 512))
        {
            config.FFmpegPath = ffmpegPath;
        }
        SaveConfigAfterItemEdit(config);
        ImGui.TextDisabled(Loc.T("Config.CurrentPath", config.GetEffectiveFFmpegPath(Plugin.PluginInterface)));
        DrawBundledFFmpegControls(config);

#if DEBUG
        bool forceFFmpeg = config.ForceFFmpegFallbackForTesting;
        if (ImGui.Checkbox(Loc.T("Config.DebugForceFFmpeg"), ref forceFFmpeg))
        {
            config.ForceFFmpegFallbackForTesting = forceFFmpeg;
            SaveConfig(config);
        }

        if (ImGui.Button(Loc.T("Config.DebugNvidiaToast"), new Vector2(-1, 0)))
            _plugin.RecordingService.ShowNvencDriverUpdateToastForTesting();
#endif

        ImGui.TextDisabled(Loc.T("Config.EncodingModeLabel", GetEncodingModeText(config)));

        if (config.VideoCodec != "auto")
        {
            string[] codecs = { "hevc_nvenc", "hevc_qsv", "hevc_amf", "h264_nvenc", "h264_qsv", "h264_amf", "libx264", "libx265" };
            int codecIdx = Array.IndexOf(codecs, config.VideoCodec);
            if (codecIdx < 0) codecIdx = 0;
            if (ImGui.Combo(Loc.T("Config.VideoEncoder"), ref codecIdx, codecs, codecs.Length))
            {
                config.VideoCodec = codecs[codecIdx];
                SaveConfig(config);
            }

            string preset = config.EncoderPreset;
            if (ImGui.InputText(Loc.T("Config.Preset"), ref preset, 64))
            {
                config.EncoderPreset = preset;
            }
            SaveConfigAfterItemEdit(config);
            ImGui.TextDisabled(Loc.T("Config.ActualPreset", config.ResolvePreset()));
        }
    }

    private void DrawDiagnosticsSettings(Configuration config)
    {
        ImGui.Text(Loc.T("Config.DiagnosticsSettings"));

        bool telemetry = config.EnablePocketBackendTelemetry;
        if (ImGui.Checkbox(Loc.T("Config.EnableTelemetry"), ref telemetry))
        {
            config.EnablePocketBackendTelemetry = telemetry;
            SaveConfig(config);
        }
        ImGui.TextDisabled(Loc.T("Config.TelemetryDesc"));
    }

    private void DrawBundledFFmpegControls(Configuration config)
    {
        bool bootstrapBusy = _plugin.IsFFmpegBootstrapRunning && !_plugin.IsFFmpegBootstrapComplete;
        bool busy = _ffmpegInstallInProgress || bootstrapBusy;

        if (busy)
        {
            ImGui.BeginDisabled();
            ImGui.Button(Loc.T("Config.Downloading"), new Vector2(-1, 0));
            ImGui.EndDisabled();
        }
        else if (ImGui.Button(Loc.T("Config.DownloadUpdateFFmpeg"), new Vector2(-1, 0)))
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
        _ffmpegInstallStatus = Loc.T("Warmup.DownloadingFFmpeg");

        var thread = new Thread(() =>
        {
            try
            {
                string installedPath = FFmpegBootstrapper.InstallOrUpdateBundled(
                    Plugin.PluginInterface.GetPluginConfigDirectory());
                config.FFmpegPath = installedPath;
                config.Save(Plugin.PluginInterface);
                _ffmpegInstallStatus = Loc.T("Warmup.FFmpegInstalled", installedPath);
            }
            catch (Exception ex)
            {
                Plugin.Log!.Error($"[FFmpeg] Manual install/update failed: {ex}");
                _ffmpegInstallStatus = Loc.T("Warmup.FFmpegDownloadFailed", ex.Message);
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
            return config.EffectiveForceFFmpegFallbackForTesting ? Loc.T("Config.EncodingModeFFmpegTest") : Loc.T("Config.EncodingModeFFmpeg");

        if (config.VideoCodec != "auto")
            return Loc.T("Config.EncodingModeAdvanced", config.VideoCodec);

        return config.UseHardwareEncoder
            ? Loc.T("Config.EncodingModeAutoHw")
            : Loc.T("Config.EncodingModeCompatible");
    }

    private static string GetAudioModeDescription(AudioCaptureMode mode)
    {
        return mode switch
        {
            AudioCaptureMode.Game => Loc.T("Config.AudioGameDesc"),
            AudioCaptureMode.System => Loc.T("Config.AudioSystemDesc"),
            _ => Loc.T("Config.AudioOffDesc"),
        };
    }

    private void DrawOutputSettings(Configuration config)
    {
        ImGui.Text(Loc.T("Config.OutputSettings"));

        string dir = config.OutputDirectory;
        if (ImGui.InputText(Loc.T("Config.OutputDirectory"), ref dir, 256))
        {
            config.OutputDirectory = dir;
        }
        SaveConfigAfterItemEdit(config);

        string effectiveDir = config.GetEffectiveOutputDirectory(Plugin.PluginInterface);
        ImGui.TextDisabled(Loc.T("Config.CurrentPath", effectiveDir));

        if (ImGui.Button(Loc.T("Config.OpenOutputDir")))
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
        if (ImGui.Button(Loc.T("Config.RecordingList")))
            _plugin.RecordingListWindow.IsOpen = true;

        int retentionDays = config.RecordingRetentionDays;
        ImGui.InputInt(Loc.T("Config.RetentionDays"), ref retentionDays);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.RecordingRetentionDays = Math.Clamp(retentionDays, 0, RecordingRetentionCleanupService.MaxRetentionDays);
            SaveConfig(config);
        }

        if (config.RecordingRetentionDays <= 0)
        {
            ImGui.TextDisabled(Loc.T("Config.CleanupOff"));
        }
        else
        {
            ImGui.TextDisabled(Loc.T("Config.CleanupDesc", config.RecordingRetentionDays));
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
