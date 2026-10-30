namespace MainCore.Errors;

// A committing action was attempted but its result could not be established.
// Only manual inspection can authorize another attempt.
public sealed class UncertainTaskOutcome(string message, ConstructionStep? construction = null) : Stop(message)
{
    public ConstructionStep? Construction { get; } = construction;
}

// Recovery could not restore a usable browser page for any task.
public sealed class BrowserRecoveryFailed(string message) : Stop(message);
