namespace TaskManagerAI.Services;

public enum AssistantOutcomeStatus
{
    Success,
    OutOfScope,
    ClarificationNeeded,
    InvalidArguments,
    NotFound,
    Ambiguous,
    ConfirmationRequired,
    Cancelled,
    Unavailable,
    CouldNotComplete
}

public class AssistantOutcome
{
    public required AssistantOutcomeStatus Status { get; init; }

    public required string Action { get; init; }

    public required string Result { get; init; }

    public object? Data { get; init; }

    public string? ConfirmationToken { get; init; }
}
