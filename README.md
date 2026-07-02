<h1 align="center">Pocket Recorder</h1>
<p align="center">
<img src="images/icon.png" width="96" alt="Pocket Recorder icon">
</p>

Pocket Recorder 是一个用于 FFXIV / Dalamud 的轻量录制插件。它提供悬浮录制按钮、自动八人本倒计时录制、聊天命令和设置窗口，可以录制游戏画面，可选择录制游戏声音、系统声音或不录制声音，并输出为 MP4 文件。

## Discord 社群

遇到问题、想反馈建议或获取更新，可以加入 Discord 社群：
[https://discord.gg/CQd4w7Bzv2](https://discord.gg/CQd4w7Bzv2)

## 功能

- 一键开始/停止录制，可使用悬浮按钮、设置窗口或聊天命令控制。
- 录制游戏画面，并可选择只录制游戏声音、录制系统声音或不录制声音。
- NVIDIA/AMD 环境下优先尝试 NativeRecorder GPU 纹理路径输出 MP4；不可用或失败时自动回退 FFmpeg。
- 支持调整目标帧率、视频码率、声音来源、输出目录和 FFmpeg 路径。
- 支持倒计时开始后自动录制，并在灭团、切换区域或登出时停止保存。
- FFmpeg 可手动指定，也可通过设置窗口下载/更新内置版本。

## 对开荒和复盘更友好

- 开荒时不用反复手动按录制，倒计时一开始就会自动开录。
- 灭团、切换区域或登出会自动停录并保存，让每一次尝试都尽量独立成段，回看时更容易对应到具体流程。
- 自动录制会按副本名和起止时间命名，录像天然就能按日期、进度和副本快速检索，整理归档更省事。

## 性能定位

PocketRecorder 面向 FFXIV/Dalamud 的专用轻量录制场景，捕获路径与 OBS 的通用窗口捕获不同。

当前版本通过 PresentHook 直接获取游戏 D3D11 backbuffer。NVIDIA 或 AMD + `NativeRecorder.dll` 可用时，Present hook 只把 backbuffer 快速复制到插件自有的 shared texture ring，然后 native DLL 打开 shared handle、在 GPU 上转 NV12，并优先通过 NvEncoderD3D11 或 AMD AMF + libavformat 写 HEVC MP4，不再经过 `ffmpeg.exe` stdin rawvideo。若 native 路径不可用、驱动编码器初始化失败或首帧提交失败，则自动回到原有 FFmpeg 路径。

FFmpeg fallback 路径会在 GPU 上将 BGRA/RGBA 转换为 NV12 后再 readback。相比直接读取 BGRA/RGBA，NV12 每帧数据量约为原来的 37.5%，显著降低了 CPU 拷贝、内存带宽和 FFmpeg stdin 管道压力。

与 OBS Window Capture 相比，PocketRecorder 路径更短，不依赖 WGC/DWM 窗口捕获层，也没有 OBS 的场景系统、预览画布、多源合成、滤镜和插件框架等常驻开销，因此在只录制 FFXIV 的场景下更轻量，通常对小内存机器更友好。

与 OBS Game Capture 相比，OBS 仍然拥有更成熟的全 GPU shared texture 编码管线；PocketRecorder 的 native 路径采用同类的资源隔离思路，但只服务 FFXIV 录制。FFmpeg fallback 仍保留 NV12 readback 和管道写入，用于兼容 native DLL 缺失或驱动/编码器不支持的机器。

简而言之：PocketRecorder 不追求覆盖 OBS 的完整通用能力，而是专注于 FFXIV 的轻量录制体验，对小内存机器更友好。

## 使用

安装并启用插件后，可以通过 `/pocketrecorder` 或短命令 `/pktr` 打开设置窗口。

常用命令：

```text
/pktr start                 开始录制
/pktr end                   停止录制
/pktr toggle                切换录制状态
/pktr status                查看录制状态
/pktr config                打开设置窗口
/pktr output                打开输出目录
/pktr autorecord on/off     开关倒计时自动录制
/pktr floating on/off       显示或隐藏悬浮录制按钮
/pktr fps 60                设置目标帧率
/pktr bitrate 32            设置视频码率，单位 Mbps
/pktr audio game/system/off 设置声音来源
```

## 输出

默认输出目录位于插件配置目录下的 `Recordings` 文件夹，也可以在设置窗口中指定其他目录。

手动录制文件名格式：

```text
FFXIV_yyyyMMdd_HHmmss.mp4
```

自动副本录制会使用副本名与起止时间命名，方便回看和归档。


## 注意事项

- 当前插件面向 Windows 环境，依赖 Dalamud API 15。
- 首次使用自动 FFmpeg 下载时需要网络访问；也可以在设置窗口中填写已有的 `ffmpeg.exe` 路径。
- 录制质量和性能取决于分辨率、帧率、码率和本机编码器能力。若硬件编码不可用，可切换到兼容模式。

## 开发与发布

NativeRecorder 的构建依赖暂时放在 `lib/native`，包含 NVIDIA Video Codec SDK、AMD AMF SDK 和 FFmpeg LGPL shared 包。正常 clone 后可以直接构建：

```powershell
msbuild NativeRecorder\NativeRecorder.vcxproj /p:Configuration=Release /p:Platform=x64
dotnet build PocketRecorder.csproj -c Release -p:Platform=x64
```

GitHub Actions 会先构建 `NativeRecorder.abi9.dll`，再打包插件，并校验发布 zip 中包含 native DLL 和 `avformat/avcodec/avutil/swresample` 运行时 DLL。

发布构建会设置 `USE_MINIMAL_FFMPEG=true`，直接使用提交在 `lib/native/ffmpeg-minimal-lgpl-shared` 下的裁剪版 LGPL FFmpeg shared 包，不会在 GitHub Actions 中重新下载或构建 FFmpeg。需要更新该依赖时，在本机运行 `scripts/Build-MinimalFfmpeg.ps1` 从 FFmpeg `n8.1.2` 生成；该包只启用 PocketRecorder native 路径需要的 MP4 muxer、AAC encoder、file protocol、基础 parser/bitstream filter 和 `swresample`。未设置该变量时，`scripts/Prepare-NativeRecorderDeps.ps1` 仍会回退到 BtbN 的通用 LGPL shared 包，方便本地开发。

## 许可

MIT

---

<p align="center">
  <sub>Made with ❤️ by Wotou · <a href="https://github.com/kanyeishere/PocketStation">GitHub</a></sub>
</p>
