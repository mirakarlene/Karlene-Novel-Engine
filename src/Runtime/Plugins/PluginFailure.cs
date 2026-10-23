namespace KNE;

/// <summary>Preserves plugin identity and the original exception across recovery reporting.</summary>
internal sealed class PluginFailureException(string source, string operation, Exception inner)
    : Exception($"Плагин «{source}»: {operation}. {inner.Message}", inner)
{
    internal string SourcePath { get; } = source;
}

/// <summary>Notifies the application even when a caller catches a plugin exception.</summary>
internal static class PluginFailure
{
    internal static event Action<PluginFailureException>? Detected;

    internal static PluginFailureException Report(Exception error, string source, string operation)
    {
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
            if (cause is PluginFailureException existing) return existing;
        var failure = new PluginFailureException(source, operation, error);
        Detected?.Invoke(failure);
        return failure;
    }

    internal static T Invoke<T>(string source, string operation, Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (error is not OperationCanceledException) { throw Report(error, source, operation); }
    }
}
