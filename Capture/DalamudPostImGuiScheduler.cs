using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Recorder.Capture;

/// <summary>
/// Resolves Dalamud's internal post-ImGui render queue without taking a compile-time
/// dependency on internal API types. The lookup is deliberately feature-detected so
/// recording can fall back to the normal pre-Present path when Dalamud changes it.
/// </summary>
internal sealed class DalamudPostImGuiScheduler
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private Func<Action, Task>? _schedule;
    private bool _permanentlyUnavailable;

    public bool TryEnqueue(Action action, out string failureReason)
    {
        failureReason = string.Empty;

        if (_permanentlyUnavailable)
        {
            failureReason = "Dalamud post-ImGui render queue is unavailable.";
            return false;
        }

        try
        {
            if (_schedule == null && !TryResolve(out failureReason))
                return false;

            _ = _schedule!(action);
            return true;
        }
        catch (Exception ex)
        {
            _schedule = null;
            failureReason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private bool TryResolve(out string failureReason)
    {
        object uiBuilder = Plugin.PluginInterface.UiBuilder;
        Type uiBuilderType = uiBuilder.GetType();

        PropertyInfo? managerProperty = uiBuilderType.GetProperty("InterfaceManagerWithScene", InstanceFlags);
        FieldInfo? managerField = uiBuilderType.GetField("interfaceManager", InstanceFlags);
        if (managerProperty == null && managerField == null)
        {
            _permanentlyUnavailable = true;
            failureReason = "Dalamud UiBuilder does not expose an interface manager accessor.";
            return false;
        }

        object? manager = managerProperty?.GetValue(uiBuilder) ?? managerField?.GetValue(uiBuilder);
        if (manager == null)
        {
            failureReason = "Dalamud interface scene is not ready.";
            return false;
        }

        MethodInfo? runAfterImGuiRender = manager.GetType().GetMethod(
            "RunAfterImGuiRender",
            InstanceFlags,
            binder: null,
            types: [typeof(Action)],
            modifiers: null);
        if (runAfterImGuiRender == null || runAfterImGuiRender.ReturnType != typeof(Task))
        {
            _permanentlyUnavailable = true;
            failureReason = "Dalamud RunAfterImGuiRender(Action) was not found.";
            return false;
        }

        _schedule = runAfterImGuiRender.CreateDelegate<Func<Action, Task>>(manager);
        failureReason = string.Empty;
        return true;
    }
}
