# Pocket Recorder
<p align="center">
<img src="images/icon.png" width="96" alt="Pocket Recorder icon">
</p>

Pocket Recorder 是一个用于 FFXIV / Dalamud 的轻量录制插件。它提供悬浮录制按钮、自动八人本倒计时录制、聊天命令和设置窗口，可以录制游戏画面与系统声音，并输出为 MP4 文件。

## Discord 社群

遇到问题、想反馈建议或获取更新，可以加入 Discord 社群：
[https://discord.gg/CQd4w7Bzv2](https://discord.gg/CQd4w7Bzv2)

## 功能

- 一键开始/停止录制，可使用悬浮按钮、设置窗口或聊天命令控制。
- 录制游戏画面，并在可用时捕获系统音频。
- 使用 FFmpeg 输出 MP4，视频编码器可自动选择硬件编码，失败时回退到兼容模式。
- 支持调整目标帧率、视频码率、音频开关、输出目录和 FFmpeg 路径。
- 支持 8 人副本倒计时开始后自动录制，并在灭团、切换区域或登出时停止保存。
- FFmpeg 可手动指定，也可通过设置窗口下载/更新内置版本。

## 对开荒和复盘更友好

- 开荒时不用反复手动按录制，8 人副本倒计时一开始就会自动开录。
- 灭团、切换区域或登出会自动停录并保存，让每一次尝试都尽量独立成段，回看时更容易对应到具体流程。
- 自动录制会按副本名和起止时间命名，录像天然就能按日期、进度和副本快速检索，整理归档更省事。

## 性能定位

PocketRecorder 面向 FFXIV/Dalamud 的专用轻量录制场景，捕获路径与 OBS 的通用窗口捕获不同。

当前版本通过 PresentHook 直接获取游戏 D3D11 backbuffer，并在 GPU 上将 BGRA/RGBA 转换为 NV12 后再 readback。相比直接读取 BGRA/RGBA，NV12 每帧数据量约为原来的 37.5%，显著降低了 CPU 拷贝、内存带宽和 FFmpeg stdin 管道压力。

与 OBS Window Capture 相比，PocketRecorder 路径更短，不依赖 WGC/DWM 窗口捕获层，也没有 OBS 的场景系统、预览画布、多源合成、滤镜和插件框架等常驻开销，因此在只录制 FFXIV 的场景下更轻量，通常对小内存机器更友好。

与 OBS Game Capture 相比，OBS 的全 GPU shared texture 编码路径仍然拥有更高的理论性能上限；PocketRecorder 当前仍有 NV12 readback 和 FFmpeg 管道写入。但在现代 CPU、内存和 PCIe 带宽下，这部分开销通常不是主要瓶颈。PocketRecorder 选择以少量可接受的 readback 成本，换取更简单、更专用、更低内存占用的录制架构。

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
/pktr autorecord on/off     开关 8 人副本自动录制
/pktr floating on/off       显示或隐藏悬浮录制按钮
/pktr fps 60                设置目标帧率
/pktr bitrate 32            设置视频码率，单位 Mbps
/pktr audio on/off          开关系统音频录制
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

## 许可

MIT

---

<p align="center">
  <sub>Made with ❤️ by Wotou · <a href="https://github.com/kanyeishere/PocketStation">GitHub</a></sub>
</p>
