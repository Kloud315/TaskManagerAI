namespace TaskManagerAI.DTOs;

public class AssistantResponse
{
    public required string Action { get; init; }

    public required string Result { get; init; }

    public object? Data { get; init; }

    public string? ConfirmationToken { get; init; }
}
