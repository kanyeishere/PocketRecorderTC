using Recorder.Capture;
using Recorder.Recording;
using System;
using System.IO;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Recorder.Encoding;

/// <summary>
/// 使用 Windows Media Foundation Sink Writer 将 H.264 视频 + AAC 音频编码到 MP4 文件。
/// 完全 Windows 原生，无外部依赖。
/// </summary>
internal sealed unsafe class MediaFoundationWriter : IOutputSink
{
    private string _outputPath = string.Empty;
    private IMFSinkWriter* _sinkWriter;
    private uint _videoStreamIndex;
    private uint _audioStreamIndex;
    private bool _mfStarted;
    private bool _finalized;
    private bool _hasAudio;
    private int _videoFps;

    // 配置
    private readonly int _videoBitrate;
    private readonly bool _useHardwareEncoder;
    private readonly bool _lowLatency;

    // 音频格式
    private int _audioSampleRate;
    private int _audioChannels;
    private int _audioBitsPerSample;
    private int _audioBlockAlign;
    private Guid _audioInputSubtype;

    public bool SupportsAudio => _hasAudio;

    public MediaFoundationWriter(int videoBitrate, bool useHardwareEncoder, bool lowLatency)
    {
        _videoBitrate = videoBitrate;
        _useHardwareEncoder = useHardwareEncoder;
        _lowLatency = lowLatency;
    }

    public void Start(VideoFormat videoFormat, AudioFormat? audioFormat)
    {
        _videoFps = videoFormat.Fps;
        _hasAudio = audioFormat != null;

        if (audioFormat != null)
        {
            _audioSampleRate = audioFormat.SampleRate;
            _audioChannels = audioFormat.Channels;
            _audioBitsPerSample = audioFormat.BitsPerSample;
            _audioBlockAlign = audioFormat.Channels * audioFormat.BitsPerSample / 8;
            _audioInputSubtype = audioFormat.IsFloat ? MfConstants.MFAudioFormat_Float : MfConstants.MFAudioFormat_PCM;
        }

        // 确保输出目录存在
        string? dir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 启动 Media Foundation
        int hr = MFStartup(MfConstants.MF_VERSION, 0);
        if (hr < 0)
            throw new InvalidOperationException($"MFStartup failed: 0x{hr:X8}");
        _mfStarted = true;

        // 创建 SinkWriter
        _sinkWriter = CreateSinkWriter(_outputPath, videoFormat, audioFormat);

        // 开始写入
        hr = _sinkWriter->BeginWriting();
        if (hr < 0)
            throw new InvalidOperationException($"BeginWriting failed: 0x{hr:X8}");

        Plugin.Log!.Info($"MediaFoundationWriter started: {videoFormat.Width}x{videoFormat.Height}@{videoFormat.Fps}fps, bitrate={_videoBitrate}");
        if (_hasAudio)
            Plugin.Log.Info($"  Audio: {_audioSampleRate}Hz, {_audioChannels}ch, {_audioBitsPerSample}bit, subtype={_audioInputSubtype}");
    }

    public void SetOutputPath(string path) => _outputPath = path;

    // 创建 SinkWriter 时传入属性以启用硬件编码
    private IMFSinkWriter* CreateSinkWriter(string path, VideoFormat videoFormat, AudioFormat? audioFormat)
    {
        // 创建属性存储
        IMFAttributes* attributes;
        int hr = MFCreateAttributes(&attributes, 4);
        if (hr < 0)
            throw new InvalidOperationException($"MFCreateAttributes failed: 0x{hr:X8}");

        try
        {
            // 启用硬件编码器
            Guid hwGuid = MfConstants.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
            hr = attributes->SetUINT32(&hwGuid, 1);
            if (hr < 0)
                Plugin.Log!.Warning($"SetUINT32(HW_TRANSFORMS) failed: 0x{hr:X8}");

            // 指定容器类型为 MP4
            Guid containerGuid = MfConstants.MF_TRANSCODE_CONTAINERTYPE;
            Guid mp4Guid = MfConstants.MFTranscodeContainerType_MP4;
            hr = attributes->SetGUID(&containerGuid, &mp4Guid);
            if (hr < 0)
                Plugin.Log!.Warning($"SetGUID(CONTAINERTYPE) failed: 0x{hr:X8}");

            Plugin.Log!.Info($"[MF] Creating SinkWriter with hardware transforms, path={path}");

            // 创建 SinkWriter
            IMFSinkWriter* sinkWriter;
            fixed (char* pPath = path)
            {
                hr = MFCreateSinkWriterFromURL(pPath, null, attributes, &sinkWriter);
            }

            if (hr < 0)
                throw new InvalidOperationException($"MFCreateSinkWriterFromURL failed: 0x{hr:X8}");

            // 配置视频流
            ConfigureVideoStream(sinkWriter, videoFormat);

            // 配置音频流
            if (audioFormat != null)
            {
                ConfigureAudioStream(sinkWriter, audioFormat);
            }

            return sinkWriter;
        }
        finally
        {
            attributes->Release();
        }
    }

    private void ConfigureVideoStream(IMFSinkWriter* sinkWriter, VideoFormat videoFormat)
    {
        int hr;

        Plugin.Log!.Info($"[MF] Configuring video stream: {videoFormat.Width}x{videoFormat.Height}@{videoFormat.Fps}fps, bitrate={_videoBitrate}");

        // ── 输出类型：H.264 ──
        IMFMediaType* outputType;
        hr = MFCreateMediaType(&outputType);
        if (hr < 0) throw new InvalidOperationException($"MFCreateMediaType(video out) failed: 0x{hr:X8}");

        try
        {
            SetGuid(outputType, MfConstants.MF_MT_MAJOR_TYPE, MfConstants.MFMediaType_Video);
            SetGuid(outputType, MfConstants.MF_MT_SUBTYPE, MfConstants.MFVideoFormat_H264);
            SetUInt32(outputType, MfConstants.MF_MT_INTERLACE_MODE, MfConstants.MFVideoInterlace_Progressive);
            SetUInt64(outputType, MfConstants.MF_MT_FRAME_SIZE, MfConstants.PackFrameSize((uint)videoFormat.Width, (uint)videoFormat.Height));
            SetUInt64(outputType, MfConstants.MF_MT_FRAME_RATE, MfConstants.PackFrameRate((uint)videoFormat.Fps, 1));
            SetUInt64(outputType, MfConstants.MF_MT_PIXEL_ASPECT_RATIO, MfConstants.PackFrameSize(1, 1)); // 1:1 PAR

            // H.264 High Profile + Level 5.1 (支持 4K@60fps)
            SetUInt32(outputType, MfConstants.MF_MT_MPEG2_PROFILE, MfConstants.eAVEncH264VProfile_High);
            SetUInt32(outputType, MfConstants.MF_MT_MPEG2_LEVEL, MfConstants.eAVEncH264VLevel5_1);

            if (_videoBitrate > 0)
                SetUInt32(outputType, MfConstants.MF_MT_AVG_BITRATE, (uint)_videoBitrate);

            uint vIdx;
            hr = sinkWriter->AddStream(outputType, &vIdx);
            _videoStreamIndex = vIdx;
            if (hr < 0) throw new InvalidOperationException($"AddStream(video) failed: 0x{hr:X8} (profile=High, level=5.1, {videoFormat.Width}x{videoFormat.Height}@{videoFormat.Fps})");
            Plugin.Log.Info($"[MF] Video stream added: index={vIdx}");
        }
        finally
        {
            outputType->Release();
        }

        // ── 输入类型：RGB32 (BGRA) ──
        IMFMediaType* inputType;
        hr = MFCreateMediaType(&inputType);
        if (hr < 0) throw new InvalidOperationException($"MFCreateMediaType(video in) failed: 0x{hr:X8}");

        try
        {
            SetGuid(inputType, MfConstants.MF_MT_MAJOR_TYPE, MfConstants.MFMediaType_Video);
            SetGuid(inputType, MfConstants.MF_MT_SUBTYPE, MfConstants.MFVideoFormat_RGB32);
            SetUInt32(inputType, MfConstants.MF_MT_INTERLACE_MODE, MfConstants.MFVideoInterlace_Progressive);
            SetUInt64(inputType, MfConstants.MF_MT_FRAME_SIZE, MfConstants.PackFrameSize((uint)videoFormat.Width, (uint)videoFormat.Height));
            SetUInt64(inputType, MfConstants.MF_MT_FRAME_RATE, MfConstants.PackFrameRate((uint)videoFormat.Fps, 1));
            SetUInt32(inputType, MfConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            SetUInt32(inputType, MfConstants.MF_MT_FIXED_SIZE_SAMPLES, 1);
            // 设置 stride（正 = top-down）
            SetUInt32(inputType, MfConstants.MF_MT_DEFAULT_STRIDE, (uint)(videoFormat.Width * 4));

            hr = sinkWriter->SetInputMediaType(_videoStreamIndex, inputType, null);
            if (hr < 0) throw new InvalidOperationException($"SetInputMediaType(video) failed: 0x{hr:X8}");
            Plugin.Log.Info($"[MF] Video input type set: RGB32, stride={videoFormat.Width * 4}");
        }
        finally
        {
            inputType->Release();
        }
    }

    private void ConfigureAudioStream(IMFSinkWriter* sinkWriter, AudioFormat audioFormat)
    {
        int hr;

        // ── 输出类型：AAC ──
        IMFMediaType* outputType;
        hr = MFCreateMediaType(&outputType);
        if (hr < 0) throw new InvalidOperationException($"MFCreateMediaType(audio out) failed: 0x{hr:X8}");

        try
        {
            SetGuid(outputType, MfConstants.MF_MT_MAJOR_TYPE, MfConstants.MFMediaType_Audio);
            SetGuid(outputType, MfConstants.MF_MT_SUBTYPE, MfConstants.MFAudioFormat_AAC);
            SetUInt32(outputType, MfConstants.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)audioFormat.SampleRate);
            SetUInt32(outputType, MfConstants.MF_MT_AUDIO_NUM_CHANNELS, (uint)audioFormat.Channels);
            SetUInt32(outputType, MfConstants.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetUInt32(outputType, MfConstants.MF_MT_AUDIO_BLOCK_ALIGNMENT, 1);
            SetUInt32(outputType, MfConstants.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000); // ~128kbps AAC

            uint aIdx;
            hr = sinkWriter->AddStream(outputType, &aIdx);
            _audioStreamIndex = aIdx;
            if (hr < 0) throw new InvalidOperationException($"AddStream(audio) failed: 0x{hr:X8}");
        }
        finally
        {
            outputType->Release();
        }

        // ── 输入类型：PCM 或 Float（匹配 WASAPI 输出）──
        IMFMediaType* inputType;
        hr = MFCreateMediaType(&inputType);
        if (hr < 0) throw new InvalidOperationException($"MFCreateMediaType(audio in) failed: 0x{hr:X8}");

        try
        {
            Guid inputSubtype = audioFormat.IsFloat ? MfConstants.MFAudioFormat_Float : MfConstants.MFAudioFormat_PCM;
            int blockAlign = audioFormat.Channels * audioFormat.BitsPerSample / 8;
            int avgBytesPerSec = blockAlign * audioFormat.SampleRate;

            SetGuid(inputType, MfConstants.MF_MT_MAJOR_TYPE, MfConstants.MFMediaType_Audio);
            SetGuid(inputType, MfConstants.MF_MT_SUBTYPE, inputSubtype);
            SetUInt32(inputType, MfConstants.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)audioFormat.SampleRate);
            SetUInt32(inputType, MfConstants.MF_MT_AUDIO_NUM_CHANNELS, (uint)audioFormat.Channels);
            SetUInt32(inputType, MfConstants.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)audioFormat.BitsPerSample);
            SetUInt32(inputType, MfConstants.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)blockAlign);
            SetUInt32(inputType, MfConstants.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)avgBytesPerSec);
            SetUInt32(inputType, MfConstants.MF_MT_FIXED_SIZE_SAMPLES, 1);

            hr = sinkWriter->SetInputMediaType(_audioStreamIndex, inputType, null);
            if (hr < 0) throw new InvalidOperationException($"SetInputMediaType(audio) failed: 0x{hr:X8}");
        }
        finally
        {
            inputType->Release();
        }
    }

    public void WriteVideoFrame(VideoFrame frame)
    {
        if (_sinkWriter == null || _finalized) return;

        IMFSample* sample = CreateSampleFromBuffer(frame.Data, frame.TimestampHns);
        if (sample == null) return;

        try
        {
            // 设置帧持续时间（100ns 单位）
            long duration = 10_000_000L / _videoFps;
            sample->SetSampleDuration(duration);

            int hr = _sinkWriter->WriteSample(_videoStreamIndex, sample);
            if (hr < 0)
            {
                Plugin.Log!.Warning($"WriteSample(video) failed: 0x{hr:X8}");
            }
        }
        finally
        {
            sample->Release();
        }
    }

    public void WriteAudioPacket(AudioPacket packet)
    {
        if (_sinkWriter == null || _finalized || !_hasAudio) return;

        IMFSample* sample = CreateSampleFromBuffer(packet.Data, packet.TimestampHns);
        if (sample == null) return;

        try
        {
            // 计算音频持续时间（100ns）
            int blockAlign = packet.Channels * packet.BitsPerSample / 8;
            if (blockAlign > 0 && packet.SampleRate > 0)
            {
                long duration = (long)((double)packet.Data.Length / blockAlign * 10_000_000.0 / packet.SampleRate);
                sample->SetSampleDuration(duration);
            }

            int hr = _sinkWriter->WriteSample(_audioStreamIndex, sample);
            if (hr < 0)
            {
                Plugin.Log!.Warning($"WriteSample(audio) failed: 0x{hr:X8}");
            }
        }
        finally
        {
            sample->Release();
        }
    }

    public void Stop()
    {
        if (_sinkWriter == null) return;

        try
        {
            int hr = _sinkWriter->Finalize();
            if (hr < 0)
                Plugin.Log!.Warning($"Finalize failed: 0x{hr:X8}");
            _finalized = true;
            Plugin.Log.Info("MediaFoundationWriter finalized.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"MediaFoundationWriter.Stop error: {ex}");
        }
    }

    private IMFSample* CreateSampleFromBuffer(byte[] data, long timestampHns)
    {
        int hr;

        // 创建内存缓冲区
        IMFMediaBuffer* buffer;
        hr = MFCreateMemoryBuffer((uint)data.Length, &buffer);
        if (hr < 0)
        {
            Plugin.Log!.Warning($"MFCreateMemoryBuffer failed: 0x{hr:X8}");
            return null;
        }

        // 锁定缓冲区并写入数据
        byte* pData = null;
        uint maxLen = 0;
        uint curLen = 0;
        hr = buffer->Lock(&pData, &maxLen, &curLen);
        if (hr < 0 || pData == null)
        {
            buffer->Release();
            return null;
        }

        try
        {
            Marshal.Copy(data, 0, (IntPtr)pData, data.Length);
            buffer->SetCurrentLength((uint)data.Length);
        }
        finally
        {
            buffer->Unlock();
        }

        // 创建 Sample 并添加缓冲区
        IMFSample* sample;
        hr = MFCreateSample(&sample);
        if (hr < 0)
        {
            buffer->Release();
            return null;
        }

        hr = sample->AddBuffer(buffer);
        buffer->Release(); // sample 持有引用
        if (hr < 0)
        {
            sample->Release();
            return null;
        }

        sample->SetSampleTime(timestampHns);
        return sample;
    }

    // ── IMFAttributes 辅助方法 ──────────────────────────────
    private static void SetGuid(IMFMediaType* mt, Guid key, Guid value)
    {
        Guid k = key;
        Guid v = value;
        int hr = mt->SetGUID(&k, &v);
        if (hr < 0)
            throw new InvalidOperationException($"SetGUID({key}) failed: 0x{hr:X8}");
    }

    private static void SetUInt32(IMFMediaType* mt, Guid key, uint value)
    {
        Guid k = key;
        int hr = mt->SetUINT32(&k, value);
        if (hr < 0)
            throw new InvalidOperationException($"SetUINT32({key}) failed: 0x{hr:X8}");
    }

    private static void SetUInt64(IMFMediaType* mt, Guid key, ulong value)
    {
        Guid k = key;
        int hr = mt->SetUINT64(&k, value);
        if (hr < 0)
            throw new InvalidOperationException($"SetUINT64({key}) failed: 0x{hr:X8}");
    }

    public void Dispose()
    {
        if (_sinkWriter != null)
        {
            if (!_finalized)
            {
                try { _sinkWriter->Finalize(); } catch { }
            }
            _sinkWriter->Release();
            _sinkWriter = null;
        }

        if (_mfStarted)
        {
            MFShutdown();
            _mfStarted = false;
        }
    }
}
