using Dalamud.Configuration;
using Dalamud.Plugin;
using Recorder.Encoding;
using System;

namespace Recorder;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 15;

    /// <summary>录制文件输出目录，空则使用插件配置目录下的 Recordings 子目录。</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>目标视频码率（bps），0 表示由编码器自动选择。</summary>
    public int VideoBitrate { get; set; } = 32_000_000;

    /// <summary>目标帧率上限。</summary>
    public int TargetFps { get; set; } = 60;

    /// <summary>是否录制音频。</summary>
    public bool CaptureAudio { get; set; } = true;

    /// <summary>音频录制来源。</summary>
    public AudioCaptureMode AudioCaptureMode { get; set; } = AudioCaptureMode.Game;

    /// <summary>是否优先使用硬件编码器。</summary>
    public bool UseHardwareEncoder { get; set; } = true;

    /// <summary>历史配置项；NativeRecorder 现在固定优先启用，不再暴露 UI 开关。</summary>
    public bool EnableNativeRecorderExperimental { get; set; } = true;

    /// <summary>本地测试开关；启用时强制跳过 NativeRecorder，走 FFmpeg fallback。</summary>
    public bool ForceFFmpegFallbackForTesting { get; set; } = false;

    internal bool EffectiveForceFFmpegFallbackForTesting
    {
        get
        {
#if DEBUG
            return ForceFFmpegFallbackForTesting;
#else
            return false;
#endif
        }
    }

    /// <summary>FFmpeg 可执行文件路径，空则从 PATH 查找。</summary>
    public string FFmpegPath { get; set; } = string.Empty;

    /// <summary>视频编码器（如 h264_nvenc / hevc_nvenc / libx264）。</summary>
    public string VideoCodec { get; set; } = "auto";

    /// <summary>编码器预设（auto 表示按实际编码器自动选择）。</summary>
    public string EncoderPreset { get; set; } = "auto";

    /// <summary>低延迟模式（为 WebRTC 推流预留，启用后更短的 GOP 和更低的延迟）。</summary>
    public bool LowLatencyMode { get; set; } = false;

    /// <summary>是否在录制中包含 Dalamud 界面（Overlay）。</summary>
    public bool IncludeOverlay { get; set; } = false;

    /// <summary>录制开始/停止快捷键命令文本。</summary>
    public string ToggleCommand { get; set; } = "/pocketrecorder";

    /// <summary>是否显示一键录制悬浮图标。</summary>
    public bool ShowFloatingRecordButton { get; set; } = true;

    /// <summary>是否在倒计时开始时自动录制。</summary>
    public bool AutoRecordEightPlayerDuty { get; set; } = true;

    /// <summary>悬浮录制按钮位置。</summary>
    public System.Numerics.Vector2 FloatingRecordButtonPosition { get; set; } = new(48f, 180f);

    /// <summary>是否已经保存过悬浮录制按钮位置。</summary>
    public bool HasFloatingRecordButtonPosition { get; set; } = false;

    public static Configuration Load(IDalamudPluginInterface pi)
    {
        var config = (pi.GetPluginConfig() as Configuration) ?? new Configuration();
        ConfigurationMigrator.Migrate(config, pi);
        return config;
    }

    public void Save(IDalamudPluginInterface pi)
    {
        pi.SavePluginConfig(this);
    }

    public string GetEffectiveOutputDirectory(IDalamudPluginInterface pi)
    {
        return string.IsNullOrWhiteSpace(OutputDirectory)
            ? System.IO.Path.Combine(pi.GetPluginConfigDirectory(), "Recordings")
            : OutputDirectory;
    }

    /// <summary>获取有效的 FFmpeg 路径。空则返回插件目录下的自动安装路径。</summary>
    public string GetEffectiveFFmpegPath(IDalamudPluginInterface? pi = null)
    {
        if (!string.IsNullOrWhiteSpace(FFmpegPath))
            return FFmpegPath;

        return pi != null
            ? FFmpegBootstrapper.GetInstalledFFmpegPath(pi.GetPluginConfigDirectory())
            : "ffmpeg";
    }

    /// <summary>解析视频编码器。auto 模式在录制开始时由 FFmpegEncoderSelector 探测。</summary>
    public string ResolveVideoCodec()
    {
        if (VideoCodec != "auto")
            return VideoCodec;

        return UseHardwareEncoder ? "auto" : "libx264";
    }

    /// <summary>解析编码器预设。</summary>
    public string ResolvePreset()
    {
        var codec = ResolveVideoCodec();
        if (codec == "auto")
            return "auto";

        return FFmpegEncoderSelector.ResolvePresetForCodec(codec, EncoderPreset);
    }
}
