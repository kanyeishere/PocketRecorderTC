using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Threading;

namespace Recorder.Capture;

/// <summary>
/// 使用 NAudio 的 WasapiLoopbackCapture 捕获系统默认音频输出设备的回放流。
/// NAudio 是成熟的 .NET 音频库，正确处理了 WASAPI COM 互操作的所有细节。
/// </summary>
internal sealed class AudioCaptureService : IDisposable
{
    private readonly Action<AudioPacket> _onAudio;
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private WasapiLoopbackCapture? _capture;
    private bool _running;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BitsPerSample { get; private set; }

    public string? LastError { get; private set; }
    public bool Initialized { get; private set; }

    // 累计已采集的样本数，用于计算时间戳
    private long _totalSamples;
    private static readonly Stopwatch _sw = Stopwatch.StartNew();

    public AudioCaptureService(Action<AudioPacket> onAudio)
    {
        _onAudio = onAudio;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _running = true;
        _captureThread = new Thread(() => CaptureLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "Recorder-AudioCapture",
        };
        _captureThread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }

        try { _capture?.StopRecording(); } catch { }

        _captureThread?.Join(3000);
        _cts?.Dispose();
        _cts = null;
        _captureThread = null;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        Plugin.Log!.Info("[Audio] Starting NAudio WasapiLoopbackCapture...");

        try
        {
            // NAudio 内部处理 COM 初始化，不需要手动 CoInitializeEx
            _capture = new WasapiLoopbackCapture();

            // 获取音频格式
            var wf = _capture.WaveFormat;
            SampleRate = wf.SampleRate;
            Channels = wf.Channels;
            BitsPerSample = wf.BitsPerSample;
            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;

            Plugin.Log.Info($"[Audio] WaveFormat: {SampleRate}Hz, {Channels}ch, {BitsPerSample}bit, encoding={wf.Encoding}, float={isFloat}");
            Plugin.Log.Info($"[Audio] BlockAlign={wf.BlockAlign}, AvgBytesPerSec={wf.AverageBytesPerSecond}");

            Initialized = true;
            _totalSamples = 0;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            Plugin.Log.Info("[Audio] Starting recording...");
            _capture.StartRecording();
            Plugin.Log.Info("[Audio] Recording started successfully.");

            // 等待取消
            while (_running && !ct.IsCancellationRequested)
            {
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Plugin.Log!.Error($"[Audio] NAudio initialization failed: {ex}");
        }
        finally
        {
            try { _capture?.StopRecording(); } catch { }

            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            Plugin.Log!.Info("[Audio] Capture thread exiting.");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || e.BytesRecorded == 0) return;

        int blockAlign = Channels * BitsPerSample / 8;
        if (blockAlign <= 0) return;

        // 复制数据
        byte[] buffer = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, buffer, e.BytesRecorded);

        // 计算时间戳（基于已采集样本数）
        long samplesInPacket = e.BytesRecorded / blockAlign;
        long timestampHns = _totalSamples * 10_000_000L / SampleRate;
        _totalSamples += samplesInPacket;

        _onAudio(new AudioPacket(buffer, SampleRate, Channels, BitsPerSample, timestampHns));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Plugin.Log!.Warning($"[Audio] Recording stopped with exception: {e.Exception}");
        }
        else
        {
            Plugin.Log!.Info("[Audio] Recording stopped normally.");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>一包音频数据。</summary>
internal sealed record AudioPacket(byte[] Data, int SampleRate, int Channels, int BitsPerSample, long TimestampHns);
