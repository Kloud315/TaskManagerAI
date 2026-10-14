using TaskManagerAI.Models;

namespace TaskManagerAI.Services;

public sealed class ListTasksExecution
{
    public required List<TaskItem> Tasks { get; init; }

    public List<TaskItem>? TitleMatches { get; init; }

    public AssistantOutcome? Error { get; init; }
}
