using Dalamud.Plugin.Services;
using System;
using System.Diagnostics;
using System.Threading;

namespace Recorder.Capture;

internal sealed class GameGraphicsDeviceProbeCache : IDisposable
{
    private const int WarmupAttempts = 20;
    private const int WarmupRetryDelayMs = 500;

    private readonly IFramework _framework;
    private readonly IRecorderLogger _log;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _warmupThread;
    private GameGraphicsDeviceProbeResult? _cachedResult;
    private int _warmupStarted;
    private bool _disposed;

    public GameGraphicsDeviceProbeCache(IFramework framework, IRecorderLogger log)
    {
        _framework = framework;
        _log = log;
    }

    public void StartWarmup()
    {
        if (Interlocked.Exchange(ref _warmupStarted, 1) != 0)
            return;

        _warmupThread = new Thread(WarmupWorker)
        {
            IsBackground = true,
            Name = "Recorder-D3DDeviceWarmup",
        };
        _warmupThread.Start();
    }

    public GameGraphicsDeviceProbeResult Snapshot(string probeReason)
    {
        lock (_sync)
        {
            if (_cachedResult is { } result)
                return result.WithProbeReason($"{probeReason}; cachedFrom={result.ProbeReason}");
        }

        return GameGraphicsDeviceProbeResult.Unavailable(
            "startup game D3D11 device probe is not ready; using FFmpeg fallback to avoid recording-start stall",
            probeReason);
    }

    private void WarmupWorker()
    {
        Stopwatch sw = Stopwatch.StartNew();
        for (int attempt = 1; attempt <= WarmupAttempts && !_cts.IsCancellationRequested; attempt++)
        {
            GameGraphicsDeviceProbeResult result = ProbeOnFrameworkThread(attempt);
            Store(result);

            if (result.Available)
            {
                _log.Info($"[D3DProbe] Startup warmup ready after {attempt} attempt(s), elapsed={sw.ElapsedMilliseconds}ms: {result.DiagnosticSummary}");
                return;
            }

            if (attempt == 1 || attempt == WarmupAttempts)
                _log.Info($"[D3DProbe] Startup warmup attempt {attempt}/{WarmupAttempts} unavailable: {result.DiagnosticSummary}");

            if (_cts.Token.WaitHandle.WaitOne(WarmupRetryDelayMs))
                return;
        }
    }

    private GameGraphicsDeviceProbeResult ProbeOnFrameworkThread(int attempt)
    {
        try
        {
            return _framework
                .RunOnFrameworkThread(() => GameGraphicsDeviceProbe.Probe($"plugin startup warmup attempt {attempt}"))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return GameGraphicsDeviceProbeResult.Unavailable(
                $"startup game D3D11 device probe exception: {ex.Message}",
                $"plugin startup warmup attempt {attempt}");
        }
    }

    private void Store(GameGraphicsDeviceProbeResult result)
    {
        lock (_sync)
            _cachedResult = result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        try { _warmupThread?.Join(1_000); } catch { }
        _cts.Dispose();
    }
}
