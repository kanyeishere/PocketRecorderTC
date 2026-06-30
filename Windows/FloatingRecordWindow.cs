using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Recorder.Recording;
using System.Numerics;

namespace Recorder.Windows;

internal sealed class FloatingRecordWindow : Window
{
    private readonly Plugin _plugin;
    private readonly Vector2 _buttonSize = new(58f, 58f);
    private readonly Vector2 _windowPadding = new(7f, 7f);
    private bool _wasDragging;
    private bool _rightDragStartedOnButton;
    private Vector2 _rightDragStartMousePos;
    private Vector2 _rightDragStartWindowPos;
    private bool _suppressContextMenuThisFrame;

    public FloatingRecordWindow(Plugin plugin) : base("Pocket Recorder###PocketRecorderFloating")
    {
        _plugin = plugin;
        IsOpen = _plugin.Config.ShowFloatingRecordButton;
        Size = new Vector2(72, 72);
        SizeCondition = ImGuiCond.Always;
        Position = _plugin.Config.FloatingRecordButtonPosition;
        PositionCondition = _plugin.Config.HasFloatingRecordButtonPosition
            ? ImGuiCond.FirstUseEver
            : ImGuiCond.Appearing;
        BgAlpha = 0f;
        Flags = ImGuiWindowFlags.NoDecoration |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoBackground;
    }

    public override bool DrawConditions() =>
        _plugin.Config.ShowFloatingRecordButton;

    public override void Draw()
    {
        IsOpen = _plugin.Config.ShowFloatingRecordButton;

        bool ffmpegBusy = _plugin.IsFFmpegBootstrapRunning && !_plugin.IsFFmpegBootstrapComplete;
        var phase = _plugin.RecordingService.Phase;
        bool active = phase is RecordingPhase.Preparing or RecordingPhase.Recording;
        bool busy = phase == RecordingPhase.Finalizing || ffmpegBusy;
        _suppressContextMenuThisFrame = false;
        bool pressed = DrawCyberRecordButton(active, busy);

        if (pressed && !busy)
            _plugin.RecordingService.ToggleRecording();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(
                ffmpegBusy
                    ? "正在下载必要组件"
                    : active
                        ? "- 左键停止录制\n- 右键单击打开菜单\n- 右键按住拖动"
                        : busy
                            ? "保存中"
                            : "- 左键开始录制\n- 右键单击打开菜单\n- 右键按住拖动");
            ImGui.EndTooltip();
        }

        HandleRightMouseDrag();

        if (!_suppressContextMenuThisFrame &&
            ImGui.BeginPopupContextItem("PocketRecorderFloatingMenu", ImGuiPopupFlags.MouseButtonRight))
        {
            if (ImGui.MenuItem("打开设置"))
                _plugin.ConfigWindow.IsOpen = true;

            if (ImGui.MenuItem("重置位置"))
            {
                _plugin.Config.FloatingRecordButtonPosition = new Vector2(48f, 180f);
                _plugin.Config.HasFloatingRecordButtonPosition = true;
                Position = _plugin.Config.FloatingRecordButtonPosition;
                PositionCondition = ImGuiCond.Always;
                _plugin.Config.Save(Plugin.PluginInterface);
            }

            bool show = _plugin.Config.ShowFloatingRecordButton;
            if (ImGui.MenuItem("显示悬浮按钮", string.Empty, show))
            {
                _plugin.Config.ShowFloatingRecordButton = !show;
                _plugin.Config.Save(Plugin.PluginInterface);
            }

            ImGui.EndPopup();
        }
    }

    private bool DrawCyberRecordButton(bool active, bool busy)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        try
        {
            ImGui.SetCursorPos(_windowPadding);
            ImGui.InvisibleButton("##PocketRecorderCyberButton", _buttonSize);
            bool pressed = ImGui.IsItemClicked(ImGuiMouseButton.Left);
            bool hovered = ImGui.IsItemHovered();
            bool held = ImGui.IsItemActive();

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var center = (min + max) * 0.5f;
            var draw = ImGui.GetWindowDrawList();

            uint shadow = Color(0.00f, 0.00f, 0.00f, 0.45f);
            uint shell = held
                ? Color(0.04f, 0.09f, 0.15f, 0.98f)
                : Color(0.03f, 0.05f, 0.10f, 0.95f);
            uint panel = Color(0.05f, 0.11f, 0.18f, hovered ? 0.98f : 0.90f);
            uint cyan = Color(0.05f, hovered ? 0.95f : 0.80f, 0.95f, 1f);
            uint magenta = Color(0.92f, 0.15f, 0.75f, active ? 0.95f : 0.62f);
            uint green = Color(0.08f, 0.92f, 0.58f, 1f);
            uint red = Color(1.00f, 0.18f, 0.28f, 1f);
            uint muted = Color(0.60f, 0.70f, 0.78f, 1f);

            draw.AddRectFilled(min + new Vector2(3f, 4f), max + new Vector2(3f, 4f), shadow, 16f);
            draw.AddRectFilled(min, max, shell, 16f);
            draw.AddRect(min + new Vector2(0.5f, 0.5f), max - new Vector2(0.5f, 0.5f), cyan, 16f, ImDrawFlags.None, 1.35f);
            draw.AddRect(min + new Vector2(4f, 4f), max - new Vector2(4f, 4f), magenta, 12f, ImDrawFlags.None, 1.1f);

            var panelMin = min + new Vector2(10f, 10f);
            var panelMax = max - new Vector2(10f, 10f);
            draw.AddRectFilled(panelMin, panelMax, panel, 10f);
            DrawCornerTicks(draw, panelMin, panelMax, cyan, magenta);

            if (busy)
            {
                draw.AddCircle(center, 11f, muted, 28, 2.2f);
                draw.AddCircle(center, 17f, Color(0.35f, 0.45f, 0.55f, 0.34f), 32, 1.5f);
            }
            else if (active)
            {
                var half = new Vector2(8.5f, 8.5f);
                draw.AddRectFilled(center - half, center + half, red, 3f);
                draw.AddRect(center - new Vector2(15f, 15f), center + new Vector2(15f, 15f), Color(1f, 0.18f, 0.28f, 0.42f), 8f, ImDrawFlags.None, 1.7f);
            }
            else
            {
                draw.AddCircleFilled(center, 9.5f, green, 32);
                draw.AddCircle(center, 15.5f, Color(0.08f, 0.92f, 0.58f, 0.44f), 32, 2.2f);
            }

            return pressed;
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private void HandleRightMouseDrag()
    {
        if (!_rightDragStartedOnButton)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) &&
                ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                _rightDragStartedOnButton = true;
                _wasDragging = false;
                _rightDragStartMousePos = ImGui.GetMousePos();
                _rightDragStartWindowPos = Position ?? ImGui.GetWindowPos();
            }

            return;
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            if (_wasDragging)
            {
                _suppressContextMenuThisFrame = true;
                _plugin.Config.FloatingRecordButtonPosition = Position ?? ImGui.GetWindowPos();
                _plugin.Config.HasFloatingRecordButtonPosition = true;
                _plugin.Config.Save(Plugin.PluginInterface);
            }

            _rightDragStartedOnButton = false;
            _wasDragging = false;
            return;
        }

        var dragDelta = ImGui.GetMousePos() - _rightDragStartMousePos;
        if (!_wasDragging && dragDelta.LengthSquared() < 16f)
            return;

        _wasDragging = true;
        Position = _rightDragStartWindowPos + dragDelta;
        PositionCondition = ImGuiCond.Always;
    }

    private static void DrawCornerTicks(ImDrawListPtr draw, Vector2 min, Vector2 max, uint cyan, uint magenta)
    {
        const float length = 8f;
        const float thickness = 1.8f;

        draw.AddLine(min + new Vector2(2f, 0f), min + new Vector2(length, 0f), cyan, thickness);
        draw.AddLine(min + new Vector2(0f, 2f), min + new Vector2(0f, length), cyan, thickness);

        draw.AddLine(new Vector2(max.X - length, min.Y), new Vector2(max.X - 2f, min.Y), magenta, thickness);
        draw.AddLine(new Vector2(max.X, min.Y + 2f), new Vector2(max.X, min.Y + length), magenta, thickness);

        draw.AddLine(new Vector2(min.X + 2f, max.Y), new Vector2(min.X + length, max.Y), magenta, thickness);
        draw.AddLine(new Vector2(min.X, max.Y - length), new Vector2(min.X, max.Y - 2f), magenta, thickness);

        draw.AddLine(max - new Vector2(length, 0f), max - new Vector2(2f, 0f), cyan, thickness);
        draw.AddLine(max - new Vector2(0f, length), max - new Vector2(0f, 2f), cyan, thickness);
    }

    private static uint Color(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
