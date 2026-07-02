# NativeRecorder

NativeRecorder is the GPU-surface recording backend for PocketRecorder.

The goal is to avoid the current `ffmpeg.exe` rawvideo stdin path:

```text
D3D11 texture -> GPU color conversion -> hardware encoder -> muxer
```

The current checked-in implementation keeps the C ABI small and routes to pluggable native backends:

```text
Present hook GPU copy -> shared D3D11 texture -> native D3D11 video processor NV12 conversion
  -> NVIDIA NvEncoderD3D11 HEVC/H.264 -> libavformat MP4 mux
  -> AMD AMF HEVC/H.264 -> libavformat MP4 mux
```

It is enabled only when PocketRecorder detects a supported NVIDIA or AMD DXGI adapter, the matching runtime is present (`nvEncodeAPI64.dll` or `amfrt64.dll`), and `NativeRecorder.dll` loads successfully. Other systems, missing DLLs, unsupported codecs, runtime failures, or first-frame submission failures automatically fall back to the existing FFmpeg rawvideo stdin path.

The NVIDIA Video Codec SDK, AMD AMF SDK, and FFmpeg shared development package are vendored under `lib/native` so a normal checkout can build the native backend without extra downloads.

To refresh or override those dependencies, run:

```powershell
.\scripts\Prepare-NativeRecorderDeps.ps1
```

The script writes `lib/native/NativeRecorderDeps.props` as a local override file. That file is ignored by git because it contains machine-specific absolute paths.

```powershell
cmake -S NativeRecorder -B NativeRecorder/build -G "Visual Studio 17 2022" -A x64 `
  -DPOCKETRECORDER_ENABLE_NVENC=ON `
  -DPOCKETRECORDER_VIDEO_CODEC_SDK_DIR=C:\SDKs\Video_Codec_SDK
cmake --build NativeRecorder/build --config Release
```

If CMake is not available, the checked-in Visual Studio project builds the runnable native DLL:

```powershell
msbuild NativeRecorder\NativeRecorder.vcxproj /p:Configuration=Release /p:Platform=x64
```

Current status:

- Stable C ABI v9 is defined in `include/pocket_recorder_native.h`.
- `pr_probe` reports D3D11 texture support on NVIDIA machines with `nvEncodeAPI64.dll` available and AMD machines with `amfrt64.dll` available.
- `pr_submit_d3d11_shared_texture` accepts the source D3D11 device plus a D3D11 shared texture handle, creates the native device on the same adapter, performs GPU NV12 conversion, and prefers HEVC or H.264 MP4 through NvEncoderD3D11 + libavformat.
- AMD uses AMF + libavformat through the same native backend interface and muxer. If AMF cannot initialize or the first frame fails, PocketRecorder falls back to FFmpeg.
- Media Foundation is intentionally not used; native failures go straight back to the managed FFmpeg fallback.
- The legacy `pr_submit_d3d11_texture` export is retained only as a non-implemented compatibility stub; PocketRecorder no longer passes the game backbuffer pointer to native code.
- `pr_submit_audio` accepts PCM/float audio packets and writes AAC when audio is enabled.
- PocketRecorder keeps `FFmpegWriter` as the compatibility fallback for unsupported systems, missing DLLs, ABI mismatches, and failed probes.

Implementation plan:

1. Prove the NvEncoderD3D11 + libavformat path in-game at 60/120/144 fps.
2. Validate the AMF + libavformat path on a real AMD GPU and tune any driver-specific encoder parameters from logs.
3. Keep HEVC as the default native codec.
4. Add Intel oneVPL behind the same internal backend and muxer interfaces.
