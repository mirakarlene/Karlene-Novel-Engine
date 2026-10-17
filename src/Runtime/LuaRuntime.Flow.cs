using System.Text.Json;

namespace KNE;

public sealed partial class LuaRuntime
{
    private sealed record FlowError(string? File, int? Line, string Trace, string Problem, FrameIdentity? Frame);

    /// <summary>Moves a failed joined branch's diagnostics into its pending error value, so a catch clears active failure state.</summary>
    public string SuspendFlowError()
    {
        var failure = scriptFailure;
        scriptFailure = null;
        currentScriptFrame = null;
        return failure is { } value ? JsonSerializer.Serialize(new FlowError(value.File, value.Line, value.Trace, value.Problem, value.Frame)) : "";
    }

    /// <summary>Restores the original branch location only if the pending branch error escapes its caller.</summary>
    public void RestoreFlowError(string context)
    {
        if (context.Length == 0) return;
        var error = JsonSerializer.Deserialize<FlowError>(context);
        if (error != null) scriptFailure = (error.File, error.Line, error.Trace, error.Problem, error.Frame);
    }
}
