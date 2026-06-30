using Dalamud.Plugin.Services;
using Recorder.Capture;
using Recorder.Encoding;
using System;
using System.Diagnostics;
using System.IO;

namespace Recorder.Recording;

/// <summary>
/// 录制协调器：管理画面捕获、音频捕获和输出编码的生命周期。
/// </summary>
internal sealed class RecordingService : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IGameInteropProvider _gameInterop;
    private readonly IFramework _framework;
    private VideoCaptureService? _videoCapture;
    private AudioCaptureService? _audioCapture;
    private FFmpegWriter? _writer;

    private bool _isRecording;
    private DateTime _recordStart;
    private int _frameCount;
    private string? _currentFilePath;

    // 配置缓存（录制开始时快照）
    private int _videoWidth;
    private int _videoHeight;
    private int _videoFps;

    public bool IsRecording => _isRecording;
    public TimeSpan Elapsed => _isRecording ? DateTime.Now - _recordStart : TimeSpan.Zero;
    public int FrameCount => _frameCount;
    public string? CurrentFilePath => _currentFilePath;

    public event Action<bool>? RecordingStateChanged;

    public RecordingService(Plugin plugin, IGameInteropProvider gameInterop, IFramework framework)
    {
        _plugin = plugin;
        _gameInterop = gameInterop;
        _framework = framework;
    }

    public void ToggleRecording()
    {
        if (_isRecording)
            StopRecording();
        else
            StartRecording();
    }

    public void StartRecording()
    {
        if (_isRecording) return;

        var config = _plugin.Config;
        _videoFps = config.TargetFps;

        // 生成输出文件路径
        string dir = config.GetEffectiveOutputDirectory(Plugin.PluginInterface);
        string fileName = $"FFXIV_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        _currentFilePath = Path.Combine(dir, fileName);

        // 创建输出 sink（FFmpeg）
        _writer = new FFmpegWriter(
            config.GetEffectiveFFmpegPath(),
            config.VideoBitrate,
            config.ResolveVideoCodec(),
            config.ResolvePreset(),
            config.UseHardwareEncoder);
        _writer.SetOutputPath(_currentFilePath);

        // 创建并启动音频捕获（如果启用），以便获取音频格式
        if (config.CaptureAudio)
        {
            Plugin.Log.Info("[Record] Starting audio capture...");
            _audioCapture = new AudioCaptureService(OnAudioPacket);
            _audioCapture.Start();
            // 等待音频初始化完成（最多 2 秒）
            Thread.Sleep(500);
            if (_audioCapture.Initialized)
                Plugin.Log.Info($"[Record] Audio initialized: {_audioCapture.SampleRate}Hz, {_audioCapture.Channels}ch, {_audioCapture.BitsPerSample}bit");
            else
            {
                Plugin.Log.Warning($"[Record] Audio init failed (LastError={_audioCapture.LastError}), continuing video-only.");
                _audioCapture.Stop();
                _audioCapture = null;
            }
        }

        // 创建并启动视频捕获
        _videoCapture = new VideoCaptureService(
            Plugin.PluginInterface.UiBuilder,
            _gameInterop,
            _framework,
            OnVideoFrame);
        _videoCapture.Start(_videoFps);

        // 实际录制开始要等第一帧确定视频尺寸后
        _isRecording = false;
        _frameCount = 0;
        Plugin.Log.Info($"[Record] Preparation started → {_currentFilePath}");
        Plugin.Log.Info($"[Record] Config: fps={_videoFps}, bitrate={config.VideoBitrate}, codec={config.ResolveVideoCodec()}, preset={config.ResolvePreset()}, audio={config.CaptureAudio}, hw={config.UseHardwareEncoder}");
    }

    private void OnVideoFrame(VideoFrame frame)
    {
        if (_writer == null) return;

        // 第一帧：确定尺寸并初始化 writer
        if (!_isRecording)
        {
            _videoWidth = frame.Width;
            _videoHeight = frame.Height;

            AudioFormat? audioFormat = null;
            if (_plugin.Config.CaptureAudio && _audioCapture != null && _audioCapture.Initialized)
            {
                audioFormat = new AudioFormat(
                    _audioCapture.SampleRate,
                    _audioCapture.Channels,
                    _audioCapture.BitsPerSample,
                    _audioCapture.BitsPerSample == 32);
                Plugin.Log.Info($"[Record] Audio format: {audioFormat.SampleRate}Hz, {audioFormat.Channels}ch, {audioFormat.BitsPerSample}bit, float={audioFormat.IsFloat}");
            }
            else
            {
                Plugin.Log.Info("[Record] No audio (disabled or init failed), video-only recording.");
            }

            try
            {
                _writer.Start(
                    new VideoFormat(_videoWidth, _videoHeight, _videoFps),
                    audioFormat);
                _isRecording = true;
                _recordStart = DateTime.Now;
                Plugin.Log!.Info($"[Record] Recording started: {_videoWidth}x{_videoHeight}@{_videoFps}fps, audio={audioFormat != null}");
                RecordingStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                Plugin.Log!.Error($"[Record] Failed to start writer: {ex}");
                _writer.Dispose();
                _writer = null;
                _videoCapture?.Stop();
                _videoCapture = null;
                _audioCapture?.Stop();
                _audioCapture = null;
                return;
            }
        }

        // 尺寸不匹配（窗口调整），跳过帧
        if (frame.Width != _videoWidth || frame.Height != _videoHeight)
        {
            Plugin.Log!.Warning($"Frame size changed {frame.Width}x{frame.Height} ≠ {_videoWidth}x{_videoHeight}, skipping.");
            return;
        }

        _writer.WriteVideoFrame(frame);
        _frameCount++;
    }

    private void OnAudioPacket(AudioPacket packet)
    {
        _writer?.WriteAudioPacket(packet);
    }

    public void StopRecording()
    {
        if (!_isRecording && _writer == null)
        {
            // 可能还在等待第一帧
            _videoCapture?.Stop();
            _videoCapture?.Dispose();
            _videoCapture = null;
            _audioCapture?.Stop();
            _audioCapture?.Dispose();
            _audioCapture = null;
            _writer?.Dispose();
            _writer = null;
            return;
        }

        Plugin.Log.Info($"[Record] Stopping... frames={_frameCount}, duration={Elapsed}");

        // 停止捕获
        _videoCapture?.Stop();
        _audioCapture?.Stop();

        // 完成 writer
        _writer?.Stop();
        _writer?.Dispose();
        _writer = null;

        // 清理
        _videoCapture?.Dispose();
        _videoCapture = null;
        _audioCapture?.Dispose();
        _audioCapture = null;

        _isRecording = false;
        RecordingStateChanged?.Invoke(false);

        Plugin.Log.Info($"[Record] Saved: {_currentFilePath}");
    }

    public void Dispose()
    {
        StopRecording();
    }
}
