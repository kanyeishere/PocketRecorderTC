using ImGuiNET;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Recorder.Localization;
using Recorder.Recording;
using System.IO;
using System.Numerics;

namespace Recorder.Windows;

internal sealed class FloatingRecordWindow : Window
{
    private readonly Plugin _plugin;

    // Base sizes (at scale = 1.0)
    private const float BaseButtonSize = 58f;
    private const float BasePadding = 7f;
    private const float BaseStarButtonWidth = 32f;
    private const float BaseStarIconSize = 25f;
    private const float BaseCompactWindow = BaseButtonSize + BasePadding * 2f;
    private const float BaseStarWindow = BaseButtonSize + BaseStarButtonWidth + BasePadding * 2f + 5f;

    private float Scale => Math.Clamp(_plugin.Config.FloatingRecordButtonScale, 0.5f, 2.0f);
    private Vector2 ButtonSize => new(BaseButtonSize * Scale);
    private Vector2 StarButtonSize => new(BaseStarButtonWidth * Scale, BaseButtonSize * Scale);
    private Vector2 StarIconSize => new(BaseStarIconSize * Scale);
    private Vector2 WindowPadding => new(BasePadding * Scale);
    private Vector2 CompactWindowSize => new(BaseCompactWindow * Scale);
    private Vector2 StarWindowSize => new(BaseStarWindow * Scale, BaseCompactWindow * Scale);

    private ISharedImmediateTexture? _starNormalTexture;
    private ISharedImmediateTexture? _starHoverTexture;
    private ISharedImmediateTexture? _starActiveTexture;
    private bool _starTextureLoadAttempted;
    private bool _wasDragging;
    private bool _rightDragStartedOnButton;
    private Vector2 _rightDragStartMousePos;
    private Vector2 _rightDragStartWindowPos;
    private bool _rightClickRequestedMenu;
    private bool _suppressContextMenuThisFrame;

    public FloatingRecordWindow(Plugin plugin) : base("Pocket Recorder###PocketRecorderFloating")
    {
        _plugin = plugin;
        IsOpen = _plugin.Config.ShowFloatingRecordButton;
        Size = CompactWindowSize;
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

    public void SyncOpenState()
    {
        IsOpen = ShouldShow();
    }

    public override bool DrawConditions() =>
        ShouldShow();

    public override void Draw()
    {
        SyncOpenState();

        bool ffmpegBusy = _plugin.IsFFmpegBootstrapRunning && !_plugin.IsFFmpegBootstrapComplete;
        var phase = _plugin.RecordingService.Phase;
        bool active = phase is RecordingPhase.Preparing or RecordingPhase.Recording;
        bool busy = phase == RecordingPhase.Finalizing || ffmpegBusy;
        Size = active ? StarWindowSize : CompactWindowSize;
        SizeCondition = ImGuiCond.Always;
        _suppressContextMenuThisFrame = false;
        bool pressed = DrawCyberRecordButton(active, busy);

        if (pressed && !busy)
            _plugin.RecordingService.ToggleRecording();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(
                ffmpegBusy
                    ? Loc.T("Floating.TooltipDownloading")
                    : active
                        ? Loc.T("Floating.TooltipStop")
                        : busy
                            ? Loc.T("Floating.TooltipSaving")
                            : Loc.T("Floating.TooltipStart"));
            ImGui.EndTooltip();
        }

        HandleRightMouseDrag();

        if (active)
            DrawRecordingStar();

        if (_rightClickRequestedMenu)
        {
            ImGui.OpenPopup("PocketRecorderFloatingMenu");
            _rightClickRequestedMenu = false;
        }

        if (!_suppressContextMenuThisFrame &&
            ImGui.BeginPopup("PocketRecorderFloatingMenu"))
        {
            if (ImGui.MenuItem(Loc.T("Floating.OpenSettings")))
                _plugin.ConfigWindow.IsOpen = true;

            if (ImGui.MenuItem(Loc.T("Floating.RecordingList")))
                _plugin.RecordingListWindow.IsOpen = true;

            if (ImGui.MenuItem(Loc.T("Floating.OpenOutputDir")))
                OpenOutputDirectory();

            if (ImGui.MenuItem(Loc.T("Floating.ResetPosition")))
            {
                _plugin.Config.FloatingRecordButtonPosition = new Vector2(48f, 180f);
                _plugin.Config.HasFloatingRecordButtonPosition = true;
                Position = _plugin.Config.FloatingRecordButtonPosition;
                PositionCondition = ImGuiCond.Always;
                _plugin.Config.Save(Plugin.PluginInterface);
            }

            bool show = _plugin.Config.ShowFloatingRecordButton;
            if (ImGui.MenuItem(Loc.T("Floating.ShowFloatingButton"), string.Empty, show))
            {
                _plugin.Config.ShowFloatingRecordButton = !show;
                _plugin.Config.Save(Plugin.PluginInterface);
            }

            ImGui.EndPopup();
        }
    }

    private void DrawRecordingStar()
    {
        bool starred = _plugin.RecordingService.CurrentRecordingStarred;

        ImGui.SameLine(0f, 5f * Scale);
        ImGui.SetCursorPosY(WindowPadding.Y);
        ImGui.InvisibleButton("##PocketRecorderStarButton", StarButtonSize);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _plugin.RecordingService.ToggleCurrentRecordingStar();
            starred = _plugin.RecordingService.CurrentRecordingStarred;
        }

        bool hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = (min + max) * 0.5f;
        var draw = ImGui.GetWindowDrawList();

        uint starStroke = starred
            ? Color(1f, 0.96f, 0.58f, 1f)
            : Color(0.72f, hovered ? 0.95f : 0.86f, hovered ? 1f : 0.92f, 1f);
        uint starFill = Color(1f, 0.82f, 0.18f, starred ? 1f : 0f);
        uint starGlow = starred
            ? Color(1f, 0.72f, 0.15f, 0.38f)
            : Color(0.10f, 0.86f, 1f, hovered ? 0.42f : 0.22f);

        DrawStarTexture(draw, center, starred, hovered);
        DrawStarShape(draw, center, 11.2f * Scale, 5.2f * Scale, starFill, starStroke, starGlow, starred);

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(starred ? Loc.T("Floating.StarActive") : Loc.T("Floating.StarMark"));
            ImGui.EndTooltip();
        }
    }

    private void DrawStarTexture(ImDrawListPtr draw, Vector2 center, bool starred, bool hovered)
    {
        EnsureStarTextures();
        ISharedImmediateTexture? texture = starred
            ? _starActiveTexture
            : hovered
                ? _starHoverTexture
                : _starNormalTexture;

        if (texture == null)
            return;

        var wrap = texture.GetWrapOrEmpty();
        var min = center - StarIconSize * 0.5f;
        draw.AddImage(wrap.ImGuiHandle, min, min + StarIconSize);
    }

    private static void DrawStarShape(
        ImDrawListPtr draw,
        Vector2 center,
        float outerRadius,
        float innerRadius,
        uint fill,
        uint stroke,
        uint glow,
        bool filled)
    {
        Span<Vector2> points = stackalloc Vector2[10];
        for (int i = 0; i < points.Length; i++)
        {
            float radius = i % 2 == 0 ? outerRadius : innerRadius;
            float angle = (-90f + i * 36f) * MathF.PI / 180f;
            points[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }

        if (filled)
        {
            for (int i = 0; i < points.Length; i++)
                draw.AddTriangleFilled(center, points[i], points[(i + 1) % points.Length], fill);
        }

        for (int i = 0; i < points.Length; i++)
            draw.AddLine(points[i], points[(i + 1) % points.Length], glow, 4.2f);

        for (int i = 0; i < points.Length; i++)
            draw.AddLine(points[i], points[(i + 1) % points.Length], stroke, 2.2f);
    }

    private void EnsureStarTextures()
    {
        if (_starTextureLoadAttempted)
            return;

        _starTextureLoadAttempted = true;
        try
        {
            string imageDirectory = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty, "images");
            _starNormalTexture = Plugin.TextureProvider.GetFromFile(Path.Combine(imageDirectory, "star-normal.png"));
            _starHoverTexture = Plugin.TextureProvider.GetFromFile(Path.Combine(imageDirectory, "star-hover.png"));
            _starActiveTexture = Plugin.TextureProvider.GetFromFile(Path.Combine(imageDirectory, "star-active.png"));
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Warning($"[Floating] Failed to load star textures: {ex.Message}");
        }
    }

    private bool DrawCyberRecordButton(bool active, bool busy)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        try
        {
            ImGui.SetCursorPos(WindowPadding);
            ImGui.InvisibleButton("##PocketRecorderCyberButton", ButtonSize);
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

            float s = Scale;
            draw.AddRectFilled(min + new Vector2(3f * s, 4f * s), max + new Vector2(3f * s, 4f * s), shadow, 16f * s);
            draw.AddRectFilled(min, max, shell, 16f * s);
            draw.AddRect(min + new Vector2(0.5f * s, 0.5f * s), max - new Vector2(0.5f * s, 0.5f * s), cyan, 16f * s, ImDrawFlags.None, 1.35f * s);
            draw.AddRect(min + new Vector2(4f * s, 4f * s), max - new Vector2(4f * s, 4f * s), magenta, 12f * s, ImDrawFlags.None, 1.1f * s);

            var panelMin = min + new Vector2(10f * s, 10f * s);
            var panelMax = max - new Vector2(10f * s, 10f * s);
            draw.AddRectFilled(panelMin, panelMax, panel, 10f * s);
            DrawCornerTicks(draw, panelMin, panelMax, cyan, magenta, s);

            if (busy)
            {
                draw.AddCircle(center, 11f * s, muted, 28, 2.2f * s);
                draw.AddCircle(center, 17f * s, Color(0.35f, 0.45f, 0.55f, 0.34f), 32, 1.5f * s);
            }
            else if (active)
            {
                var half = new Vector2(8.5f * s, 8.5f * s);
                draw.AddRectFilled(center - half, center + half, red, 3f * s);
                draw.AddRect(center - new Vector2(15f * s, 15f * s), center + new Vector2(15f * s, 15f * s), Color(1f, 0.18f, 0.28f, 0.42f), 8f * s, ImDrawFlags.None, 1.7f * s);
            }
            else
            {
                draw.AddCircleFilled(center, 9.5f * s, green, 32);
                draw.AddCircle(center, 15.5f * s, Color(0.08f, 0.92f, 0.58f, 0.44f), 32, 2.2f * s);
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
            if (!_suppressContextMenuThisFrame)
                _rightClickRequestedMenu = true;
            return;
        }

        var dragDelta = ImGui.GetMousePos() - _rightDragStartMousePos;
        if (!_wasDragging && dragDelta.LengthSquared() < 16f)
            return;

        _wasDragging = true;
        Position = _rightDragStartWindowPos + dragDelta;
        PositionCondition = ImGuiCond.Always;
    }

    private static void DrawCornerTicks(ImDrawListPtr draw, Vector2 min, Vector2 max, uint cyan, uint magenta, float scale)
    {
        float length = 8f * scale;
        float thickness = 1.8f * scale;

        draw.AddLine(min + new Vector2(2f * scale, 0f), min + new Vector2(length, 0f), cyan, thickness);
        draw.AddLine(min + new Vector2(0f, 2f * scale), min + new Vector2(0f, length), cyan, thickness);

        draw.AddLine(new Vector2(max.X - length, min.Y), new Vector2(max.X - 2f * scale, min.Y), magenta, thickness);
        draw.AddLine(new Vector2(max.X, min.Y + 2f * scale), new Vector2(max.X, min.Y + length), magenta, thickness);

        draw.AddLine(new Vector2(min.X + 2f * scale, max.Y), new Vector2(min.X + length, max.Y), magenta, thickness);
        draw.AddLine(new Vector2(min.X, max.Y - length), new Vector2(min.X, max.Y - 2f * scale), magenta, thickness);

        draw.AddLine(max - new Vector2(length, 0f), max - new Vector2(2f * scale, 0f), cyan, thickness);
        draw.AddLine(max - new Vector2(0f, length), max - new Vector2(0f, 2f * scale), cyan, thickness);
    }

    private static uint Color(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    private void OpenOutputDirectory()
    {
        try
        {
            ShellHelpers.OpenDirectory(_plugin.Config.GetEffectiveOutputDirectory(Plugin.PluginInterface));
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Error($"Failed to open output directory: {ex}");
        }
    }

    private bool ShouldShow() =>
        _plugin.Config.ShowFloatingRecordButton;
}
