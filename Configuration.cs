using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace Recorder;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>录制文件输出目录，空则使用插件配置目录下的 Recordings 子目录。</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>目标视频码率（bps），0 表示由编码器自动选择。</summary>
    public int VideoBitrate { get; set; } = 8_000_000;

    /// <summary>目标帧率上限。</summary>
    public int TargetFps { get; set; } = 30;

    /// <summary>是否录制音频。</summary>
    public bool CaptureAudio { get; set; } = true;

    /// <summary>是否优先使用硬件编码器。</summary>
    public bool UseHardwareEncoder { get; set; } = false;

    /// <summary>FFmpeg 可执行文件路径，空则从 PATH 查找。</summary>
    public string FFmpegPath { get; set; } = string.Empty;

    /// <summary>视频编码器（如 h264_nvenc / hevc_nvenc / av1_nvenc / libx264）。</summary>
    public string VideoCodec { get; set; } = "auto";

    /// <summary>编码器预设（如 p4 / veryfast）。</summary>
    public string EncoderPreset { get; set; } = "ultrafast";

    /// <summary>低延迟模式（为 WebRTC 推流预留，启用后更短的 GOP 和更低的延迟）。</summary>
    public bool LowLatencyMode { get; set; } = false;

    /// <summary>是否在录制中包含 Dalamud 界面（Overlay）。</summary>
    public bool IncludeOverlay { get; set; } = false;

    /// <summary>录制开始/停止快捷键命令文本。</summary>
    public string ToggleCommand { get; set; } = "/record";

    public static Configuration Load(IDalamudPluginInterface pi)
    {
        var config = (pi.GetPluginConfig() as Configuration) ?? new Configuration();
        if (config.Version < 2)
        {
            config.UseHardwareEncoder = false;
            config.TargetFps = Math.Min(config.TargetFps, 30);
            if ((config.VideoCodec == "auto" || config.VideoCodec.Equals("libx264", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(config.EncoderPreset) || config.EncoderPreset.Equals("veryfast", StringComparison.OrdinalIgnoreCase) || IsNvencPreset(config.EncoderPreset)))
            {
                config.EncoderPreset = "ultrafast";
            }

            config.Version = 2;
            config.Save(pi);
        }

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

    /// <summary>获取有效的 FFmpeg 路径。空则返回 "ffmpeg"（依赖系统 PATH）。</summary>
    public string GetEffectiveFFmpegPath()
    {
        return string.IsNullOrWhiteSpace(FFmpegPath) ? "ffmpeg" : FFmpegPath;
    }

    /// <summary>解析视频编码器。auto 模式优先使用稳定的软件编码器。</summary>
    public string ResolveVideoCodec()
    {
        if (VideoCodec != "auto")
            return VideoCodec;

        return "libx264";
    }

    /// <summary>解析编码器预设。</summary>
    public string ResolvePreset()
    {
        var codec = ResolveVideoCodec();
        if (string.IsNullOrEmpty(EncoderPreset))
            return codec.StartsWith("libx") ? "ultrafast" : "p4";

        if (codec.StartsWith("libx", StringComparison.OrdinalIgnoreCase) && IsNvencPreset(EncoderPreset))
            return "ultrafast";

        return EncoderPreset;
    }

    private static bool IsNvencPreset(string preset)
    {
        return preset.Length == 2 &&
               preset[0] is 'p' or 'P' &&
               preset[1] >= '1' &&
               preset[1] <= '7';
    }
}
