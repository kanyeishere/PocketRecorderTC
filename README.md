<h1 align="center">Pocket Recorder</h1>
<p align="center">
<img src="images/icon.png" width="96" alt="Pocket Recorder icon">
</p>

Pocket Recorder 是一个用于 FFXIV / Dalamud 的轻量录制插件。它提供悬浮录制按钮、自动倒计时录制、聊天命令和设置窗口，可以录制游戏画面，可选择录制游戏声音、系统声音或不录制声音，并输出为 MP4 文件。

Pocket Recorder is a lightweight recording plugin for FFXIV / Dalamud. It provides a floating record button, automatic countdown-triggered recording, chat commands, and a settings window. It captures game video with optional game audio, system audio, or no audio, and outputs MP4 files.

## 快速开始 / Qucik Start
https://github.com/kanyeishere/PocketStationTC/releases/latest/download/pluginmaster.json

## Discord 社群 / Discord Community

遇到问题、想反馈建议或获取更新，可以加入 Discord 社群：
Got questions, feedback, or want updates? Join the Discord community:
[https://discord.gg/CQd4w7Bzv2](https://discord.gg/CQd4w7Bzv2)

## 多语言支持 / Multi-language Support

插件界面支持以下语言，可在设置窗口中手动选择，或设为「自动」跟随 Dalamud 客户端语言：
The plugin UI supports the following languages. You can manually select one in the settings window, or set it to "Auto" to follow the Dalamud client language:

- English
- 日本語 (Japanese)
- 简体中文 (Simplified Chinese)
- 繁體中文 (Traditional Chinese)

## 隐私与诊断 / Privacy & Diagnostics

插件默认开启「匿名脱敏诊断日志上报」。开启后，录制结束时会将经过脱敏处理的诊断日志（路径和标识符已被移除）发送到 Pocket Backend，帮助改进插件。不收集任何个人数据。该选项可在设置窗口的「隐私与诊断」区域随时关闭。

The plugin has "anonymous desensitized diagnostic reporting" enabled by default. When enabled, sanitized diagnostic logs (paths and identifiers are removed) are sent to the Pocket Backend after recording to help improve the plugin. No personal data is collected. This option can be turned off at any time in the "Privacy & Diagnostics" section of the settings window.

## 功能 / Features

- 一键开始/停止录制，可使用悬浮按钮、设置窗口或聊天命令控制。
  One-click start/stop recording via floating button, settings window, or chat commands.
- 录制游戏画面，并可选择只录制游戏声音、录制系统声音或不录制声音。
  Capture game video with optional game audio, system audio, or no audio.
- 默认使用 NVIDIA/AMD/Intel NativeRecorder GPU 原生录制；也可以在设置中固定使用 FFmpeg 录制。
  Defaults to GPU-native recording via NVIDIA/AMD/Intel NativeRecorder; can also force FFmpeg in settings.
- NativeRecorder 不可用或失败时会自动回退 FFmpeg。
  Automatically falls back to FFmpeg when NativeRecorder is unavailable or fails.
- 支持调整录制方式、目标帧率、视频码率、声音来源、输出目录和 FFmpeg 路径。
  Adjustable recording method, target FPS, video bitrate, audio source, output directory, and FFmpeg path.
- 可设置录像保存期限，定期清理旧录像，默认关闭。
  Optional recording retention period for automatic cleanup of old recordings (off by default).
- 支持倒计时开始后自动录制，并在灭团、切换区域或登出时停止保存。
  Auto-record on duty countdown; stops and saves on wipe, zone change, or logout.
- FFmpeg 可手动指定，也可通过设置窗口下载/更新内置版本。
  FFmpeg can be specified manually or downloaded/updated via the settings window.

## 对开荒和复盘更友好 / Great for Progression & Review

- 开荒时不用反复手动按录制，倒计时一开始就会自动开录。
  No need to manually hit record during progression — recording starts automatically on countdown.
- 灭团、切换区域或登出会自动停录并保存，让每一次尝试都尽量独立成段，回看时更容易对应到具体流程。
  Wipes, zone changes, and logouts automatically stop and save, keeping each attempt as a separate clip for easier review.
- 自动录制会按副本名和起止时间命名，录像天然就能按日期、进度和副本快速检索，整理归档更省事。
  Auto-recordings are named by duty name and start/end time, making it easy to sort by date, progress, or duty.

## 性能定位 / Performance Positioning

PocketRecorder 面向 FFXIV/Dalamud 的专用轻量录制场景，捕获路径与 OBS 的通用窗口捕获不同。

当前版本通过 PresentHook 直接获取游戏 D3D11 backbuffer。 Present hook 会像 OBS Game Capture 一样把 backbuffer 复制到插件自有的单个 shared texture，录制线程按目标输出帧率采样"当前最新共享纹理"，再由 native DLL 打开 shared handle、在 GPU 上转 NV12，并优先通过 NvEncoderD3D11、AMD AMF 或 Intel oneVPL QSV + libavformat 写 MP4，不再经过 `ffmpeg.exe` stdin rawvideo。若 native 路径不可用、驱动编码器初始化失败或首帧提交失败，则自动回到原有 FFmpeg 路径。

FFmpeg fallback 路径会在 GPU 上将 BGRA/RGBA 转换为 NV12 后再 readback。相比直接读取 BGRA/RGBA，NV12 每帧数据量约为原来的 37.5%，显著降低了 CPU 拷贝、内存带宽和 FFmpeg stdin 管道压力。

与 OBS Window Capture 相比，PocketRecorder 路径更短，不依赖 WGC/DWM 窗口捕获层，也没有 OBS 的场景系统、预览画布、多源合成、滤镜和插件框架等常驻开销，因此在只录制 FFXIV 的场景下更轻量，通常对小内存机器更友好。

与 OBS Game Capture 相比，OBS 仍然拥有更成熟的全 GPU shared texture 编码管线；PocketRecorder 的 native 路径采用同类的资源隔离思路，但只服务 FFXIV 录制。FFmpeg fallback 仍保留 NV12 readback 和管道写入，用于兼容 native DLL 缺失或驱动/编码器不支持的机器。

简而言之：PocketRecorder 不追求覆盖 OBS 的完整通用能力，而是专注于 FFXIV 的轻量录制体验，对小内存机器更友好。

---

PocketRecorder targets the dedicated lightweight recording scenario for FFXIV/Dalamud. Its capture path differs from OBS's general-purpose window capture.

The current version uses PresentHook to directly obtain the game's D3D11 backbuffer. The Present hook copies the backbuffer to a single plugin-owned shared texture (similar to OBS Game Capture). The recording thread samples the "latest shared texture" at the target output framerate, and the native DLL opens the shared handle, converts to NV12 on the GPU, and preferably writes MP4 via NvEncoderD3D11, AMD AMF, or Intel oneVPL QSV + libavformat — without going through `ffmpeg.exe` stdin rawvideo. If the native path is unavailable, the encoder fails to initialize, or the first frame submission fails, it automatically falls back to the FFmpeg path.

The FFmpeg fallback converts BGRA/RGBA to NV12 on the GPU before readback. Compared to reading BGRA/RGBA directly, NV12 reduces per-frame data volume to approximately 37.5%, significantly lowering CPU copy, memory bandwidth, and FFmpeg stdin pipe pressure.

Compared to OBS Window Capture, PocketRecorder has a shorter path, does not depend on WGC/DWM window capture, and has none of OBS's scene system, preview canvas, multi-source compositing, filters, or plugin framework overhead. It is lighter for FFXIV-only recording and generally friendlier to low-memory machines.

Compared to OBS Game Capture, OBS still has a more mature fully-GPU shared texture encoding pipeline. PocketRecorder's native path uses a similar resource isolation approach but only serves FFXIV recording. The FFmpeg fallback retains NV12 readback and pipe writing for compatibility on machines where the native DLL is missing or the driver/encoder is unsupported.

In short: PocketRecorder does not aim to cover OBS's full general-purpose capabilities. It focuses on a lightweight FFXIV recording experience that is friendlier to low-memory machines.


## 使用 / Usage

安装并启用插件后，可以通过 `/pocketrecorder` 或短命令 `/pktr` 打开设置窗口。
After installing and enabling the plugin, open the settings window with `/pocketrecorder` or the short command `/pktr`.

常用命令 / Common commands:

```text
/pktr start                 开始录制 / Start recording
/pktr end                   停止录制 / Stop recording
/pktr toggle                切换录制状态 / Toggle recording
/pktr status                查看录制状态 / Show recording status
/pktr config                打开设置窗口 / Open settings window
/pktr output                打开输出目录 / Open output directory
/pktr autorecord on/off     开关倒计时自动录制 / Toggle countdown auto-record
/pktr floating on/off       显示或隐藏悬浮录制按钮 / Show/hide floating button
/pktr fps 60                设置目标帧率 / Set target FPS
/pktr bitrate 32            设置视频码率，单位 Mbps / Set video bitrate (Mbps)
/pktr audio game/system/off 设置声音来源 / Set audio source
```

## 输出 / Output

默认输出目录位于插件配置目录下的 `Recordings` 文件夹，也可以在设置窗口中指定其他目录。
The default output directory is the `Recordings` folder under the plugin config directory. You can also specify a custom directory in the settings window.

手动录制文件名格式 / Manual recording file name format:

```text
FFXIV_yyyyMMdd_HHmmss.mp4
```

自动副本录制会使用副本名与起止时间命名，方便回看和归档。
Auto duty recordings are named by duty name and start/end time for easy review and archiving.

设置窗口中可以配置录像保存期限。默认 `0` 表示永久保存；设置为具体天数后，插件会在启动后和运行中定期清理输出目录内早于该天数的 Pocket Recorder 录像。清理只作用于输出目录顶层，不会递归子目录；录制中会自动跳过。

You can configure a recording retention period in the settings window. The default `0` means keep forever; when set to a specific number of days, the plugin periodically cleans up Pocket Recorder recordings older than that period in the output directory. Cleanup only applies to the top level of the output directory and does not recurse into subdirectories; it is automatically skipped during recording.

## 注意事项 / Notes

- 当前插件面向 Windows 环境，依赖 Dalamud API 12。
  The plugin targets Windows and requires Dalamud API 12.
- 首次使用自动 FFmpeg 下载时需要网络访问；也可以在设置窗口中填写已有的 `ffmpeg.exe` 路径。
  First-time automatic FFmpeg download requires network access; you can also specify an existing `ffmpeg.exe` path in the settings window.
- 录制质量和性能取决于分辨率、帧率、码率和本机编码器能力。若硬件编码不可用，可切换到兼容模式。
  Recording quality and performance depend on resolution, FPS, bitrate, and local encoder capabilities. If hardware encoding is unavailable, switch to compatible mode.

## 许可 / License

GPL

---

<p align="center">
  <sub>Made with ❤️ by Wotou · <a href="https://github.com/kanyeishere/PocketStation">GitHub</a></sub>
</p>
