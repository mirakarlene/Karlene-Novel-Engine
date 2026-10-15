namespace KNE;

/// <summary>A trusted C# source plugin. A source file must declare exactly one public implementation with a public parameterless constructor.</summary>
public interface IKnePlugin
{
    /// <summary>Executes this plugin synchronously inside the isolated game worker.</summary>
    /// <param name="context">Session state, logging and cooperative cancellation. Valid only during this call.</param>
    void Execute(IPluginContext context);
}

/// <summary>Small lifetime-bound bridge shared by C# plugins and the running game's scalar state.</summary>
public interface IPluginContext
{
    /// <summary>Gets the absolute directory of the running project.</summary>
    string ProjectDirectory { get; }
    /// <summary>Gets a token canceled when the current game operation or worker is stopped.</summary>
    CancellationToken CancellationToken { get; }
    /// <summary>Reads a scalar state value; missing values return null.</summary>
    /// <param name="name">A nonempty state key.</param>
    /// <returns>A string, Boolean, number, or null.</returns>
    object? Get(string name);
    /// <summary>Writes a scalar state value. Null removes the key.</summary>
    /// <param name="name">A nonempty state key.</param>
    /// <param name="value">A string, Boolean, finite number, or null.</param>
    void Set(string name, object? value);
    /// <summary>Writes one message to the game's diagnostic log.</summary>
    /// <param name="message">Message to publish.</param>
    void Log(string message);
}
