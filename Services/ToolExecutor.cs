using System.Globalization;
using System.Text.Json;
using TaskManagerAI.DTOs;
using TaskManagerAI.Models;

namespace TaskManagerAI.Services;

public class ToolExecutor
{
    private readonly ITaskService _taskService;
    private readonly ILogger<ToolExecutor> _logger;
    private readonly DeleteConfirmationStore _deleteConfirmationStore;

    public ToolExecutor(
        ITaskService taskService,
        ILogger<ToolExecutor> logger,
        DeleteConfirmationStore deleteConfirmationStore)
    {
        _taskService = taskService;
        _logger = logger;
        _deleteConfirmationStore = deleteConfirmationStore;
    }

    public async Task<ListTasksExecution> ExecuteListTasksAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (!HasOnlyProperties(arguments, "title_contains", "is_done"))
        {
            return RejectedList("list_tasks", InvalidArguments(
                "list_tasks",
                "The tool arguments must be an object containing only title_contains and is_done."));
        }

        string? titleFilter = null;
        if (arguments.TryGetProperty("title_contains", out var titleElement))
        {
            if (titleElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(titleElement.GetString())
                || titleElement.GetString()!.Length > 500)
            {
                return RejectedList("list_tasks", InvalidArguments(
                    "list_tasks",
                    "title_contains must be a non-empty string of at most 500 characters."));
            }

            titleFilter = titleElement.GetString();
        }

        bool? isDoneFilter = null;
        if (arguments.TryGetProperty("is_done", out var isDoneElement))
        {
            if (isDoneElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return RejectedList("list_tasks", InvalidArguments(
                    "list_tasks",
                    "is_done must be true or false."));
            }

            isDoneFilter = isDoneElement.GetBoolean();
        }

        var tasks = await _taskService.GetAllAsync(cancellationToken);
        if (titleFilter is not null)
        {
            tasks = tasks
                .Where(task => task.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (isDoneFilter.HasValue)
        {
            tasks = tasks.Where(task => task.IsDone == isDoneFilter.Value).ToList();
        }

        return new ListTasksExecution
        {
            Tasks = tasks,
            TitleMatches = titleFilter is null ? null : tasks
        };
    }

    public AssistantOutcome? ValidateTitleMatchesForMutation(
        List<TaskItem>? titleMatches)
    {
        return titleMatches is null ? null : ValidateTitleMatchCount(titleMatches);
    }

    public async Task<AssistantOutcome> ExecuteTaskFunctionAsync(
        FunctionCallProposal proposal,
        List<TaskItem>? titleMatches,
        CancellationToken cancellationToken = default)
    {
        AssistantOutcome outcome;
        if (proposal.Name == "create_task")
        {
            outcome = await CreateTaskAsync(proposal.Arguments, cancellationToken);
        }
        else if (proposal.Name == "update_task")
        {
            outcome = await UpdateTaskAsync(
                proposal.Arguments,
                titleMatches,
                cancellationToken);
        }
        else if (proposal.Name == "delete_task")
        {
            outcome = await DeleteTaskAsync(
                proposal.Arguments,
                titleMatches,
                cancellationToken);
        }
        else if (proposal.Name == "get_task")
        {
            outcome = await GetTaskAsync(
                proposal.Arguments,
                titleMatches,
                cancellationToken);
        }
        else
        {
            outcome = InvalidArguments(proposal.Name, "This function is not supported.");
        }

        if (outcome.Status is not (
                AssistantOutcomeStatus.Success
                or AssistantOutcomeStatus.ConfirmationRequired))
        {
            _logger.LogWarning(
                "Rejected proposed function call {FunctionName}: {Reason}",
                proposal.Name,
                outcome.Result);
        }

        return outcome;
    }

    private async Task<AssistantOutcome> CreateTaskAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(arguments, "title", "isDone", "dueDate"))
        {
            return InvalidArguments("create_task", "The tool arguments contain an unsupported field.");
        }

        if (!TryReadTitle(arguments, out var title, out var titleError))
        {
            return InvalidArguments("create_task", titleError!);
        }

        if (!TryReadOptionalBoolean(arguments, "isDone", out var isDone, out var booleanError))
        {
            return InvalidArguments("create_task", booleanError!);
        }

        if (!TryReadOptionalDate(arguments, "dueDate", out var dueDate, out _, out var dateError))
        {
            return InvalidArguments("create_task", dateError!);
        }

        var request = new TaskRequest
        {
            Title = title,
            IsDone = isDone ?? false,
            DueDate = dueDate
        };
        var task = await _taskService.CreateAsync(request, cancellationToken);

        return CreateOutcome(
            AssistantOutcomeStatus.Success,
            "create_task",
            $"Created task {task.Id}: \"{task.Title}\".",
            task);
    }

    private async Task<AssistantOutcome> UpdateTaskAsync(
        JsonElement arguments,
        List<TaskItem>? titleMatches,
        CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(arguments, "id", "title", "isDone", "dueDate"))
        {
            return InvalidArguments("update_task", "The tool arguments contain an unsupported field.");
        }

        if (!TryReadId(arguments, out var id, out var idError))
        {
            return InvalidArguments("update_task", idError!);
        }

        var matchError = CheckTitleMatch(id, titleMatches);
        if (matchError is not null)
        {
            return matchError;
        }

        var existingTask = await _taskService.GetByIdAsync(id, cancellationToken);
        if (existingTask is null)
        {
            return TaskNotFound(id);
        }

        var title = existingTask.Title;
        if (arguments.TryGetProperty("title", out var titleElement))
        {
            if (titleElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(titleElement.GetString())
                || titleElement.GetString()!.Length > 500)
            {
                return InvalidArguments(
                    "update_task",
                    "title must be a non-empty string of at most 500 characters.");
            }

            title = titleElement.GetString()!;
        }

        if (!TryReadOptionalBoolean(arguments, "isDone", out var isDone, out var booleanError))
        {
            return InvalidArguments("update_task", booleanError!);
        }

        if (!TryReadOptionalDate(
                arguments,
                "dueDate",
                out var dueDate,
                out var hasDueDate,
                out var dateError))
        {
            return InvalidArguments("update_task", dateError!);
        }

        var hasTitle = arguments.TryGetProperty("title", out _);
        var hasIsDone = arguments.TryGetProperty("isDone", out _);
        if (!hasTitle && !hasIsDone && !hasDueDate)
        {
            return InvalidArguments(
                "update_task",
                "Provide at least one of title, isDone, or dueDate to update.");
        }

        var request = new TaskRequest
        {
            Title = title,
            IsDone = isDone ?? existingTask.IsDone,
            DueDate = hasDueDate ? dueDate : existingTask.DueDate
        };

        var updated = await _taskService.UpdateAsync(id, request, cancellationToken);
        if (!updated)
        {
            return TaskNotFound(id);
        }

        existingTask.Title = request.Title;
        existingTask.IsDone = request.IsDone;
        existingTask.DueDate = request.DueDate;

        return CreateOutcome(
            AssistantOutcomeStatus.Success,
            "update_task",
            $"Updated task {id}.",
            existingTask);
    }

    private async Task<AssistantOutcome> DeleteTaskAsync(
        JsonElement arguments,
        List<TaskItem>? titleMatches,
        CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(arguments, "id"))
        {
            return InvalidArguments("delete_task", "The tool arguments contain an unsupported field.");
        }

        if (!TryReadId(arguments, out var id, out var idError))
        {
            return InvalidArguments("delete_task", idError!);
        }

        var matchError = CheckTitleMatch(id, titleMatches);
        if (matchError is not null)
        {
            return matchError;
        }

        var task = await _taskService.GetByIdAsync(id, cancellationToken);
        if (task is null)
        {
            return TaskNotFound(id);
        }

        var confirmationToken = _deleteConfirmationStore.Create(task.Id, task.Title);
        return CreateOutcome(
            AssistantOutcomeStatus.ConfirmationRequired,
            "delete_task",
            $"Please confirm deletion of task {id}: \"{task.Title}\". The confirmation expires in 5 minutes.",
            task,
            confirmationToken);
    }

    public async Task<AssistantOutcome> ConfirmDeleteAsync(
        string confirmationToken,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(confirmationToken))
        {
            return InvalidArguments("delete_task", "A confirmation token is required.");
        }

        if (!_deleteConfirmationStore.TryConsume(confirmationToken, out var pendingDelete))
        {
            return CreateOutcome(
                AssistantOutcomeStatus.NotFound,
                "delete_task",
                "The delete confirmation is invalid, expired, or has already been used.");
        }

        var task = new TaskItem
        {
            Id = pendingDelete.TaskId,
            Title = pendingDelete.Title
        };

        if (!confirm)
        {
            return CreateOutcome(
                AssistantOutcomeStatus.Cancelled,
                "delete_task",
                $"Deletion of task {task.Id} was cancelled.",
                task);
        }

        if (!await _taskService.DeleteAsync(task.Id, cancellationToken))
        {
            return TaskNotFound(task.Id);
        }

        return CreateOutcome(
            AssistantOutcomeStatus.Success,
            "delete_task",
            $"Deleted task {task.Id}: \"{task.Title}\".",
            task);
    }

    private async Task<AssistantOutcome> GetTaskAsync(
        JsonElement arguments,
        List<TaskItem>? titleMatches,
        CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(arguments, "id"))
        {
            return InvalidArguments("get_task", "The tool arguments contain an unsupported field.");
        }

        if (!TryReadId(arguments, out var id, out var idError))
        {
            return InvalidArguments("get_task", idError!);
        }

        var matchError = CheckTitleMatch(id, titleMatches);
        if (matchError is not null)
        {
            return matchError;
        }

        var task = await _taskService.GetByIdAsync(id, cancellationToken);
        if (task is null)
        {
            return TaskNotFound(id);
        }

        return CreateOutcome(
            AssistantOutcomeStatus.Success,
            "get_task",
            $"Found task {id}: \"{task.Title}\".",
            task);
    }

    private static AssistantOutcome? CheckTitleMatch(
        int id,
        List<TaskItem>? titleMatches)
    {
        if (titleMatches is null)
        {
            return null;
        }

        var matchCountOutcome = ValidateTitleMatchCount(titleMatches);
        if (matchCountOutcome is not null)
        {
            return matchCountOutcome;
        }

        if (titleMatches[0].Id != id)
        {
            return CreateOutcome(
                AssistantOutcomeStatus.InvalidArguments,
                "none",
                "The proposed task ID does not match the task found by title.");
        }

        return null;
    }

    private static AssistantOutcome? ValidateTitleMatchCount(
        List<TaskItem> titleMatches)
    {
        if (titleMatches.Count == 0)
        {
            return CreateOutcome(
                AssistantOutcomeStatus.NotFound,
                "none",
                "No task matched that title.");
        }

        if (titleMatches.Count > 1)
        {
            var choices = string.Join(
                "; ",
                titleMatches.Select(task => $"{task.Id}: \"{task.Title}\""));
            return CreateOutcome(
                AssistantOutcomeStatus.Ambiguous,
                "none",
                $"I found multiple matching tasks: {choices}. Please specify the task ID.",
                titleMatches);
        }

        return null;
    }

    private static bool TryReadTitle(
        JsonElement arguments,
        out string title,
        out string? error)
    {
        title = string.Empty;
        error = null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("title", out var titleElement)
            || titleElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(titleElement.GetString())
            || titleElement.GetString()!.Length > 500)
        {
            error = "A non-empty title of at most 500 characters is required.";
            return false;
        }

        title = titleElement.GetString()!;
        return true;
    }

    private static bool TryReadId(JsonElement arguments, out int id, out string? error)
    {
        id = 0;
        error = null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt32(out id)
            || id <= 0)
        {
            error = "A positive integer id is required.";
            return false;
        }

        return true;
    }

    private static bool TryReadOptionalBoolean(
        JsonElement arguments,
        string propertyName,
        out bool? value,
        out string? error)
    {
        value = null;
        error = null;

        if (!arguments.TryGetProperty(propertyName, out var element))
        {
            return true;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"{propertyName} must be true or false.";
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadOptionalDate(
        JsonElement arguments,
        string propertyName,
        out DateTime? value,
        out bool isPresent,
        out string? error)
    {
        value = null;
        error = null;
        isPresent = arguments.TryGetProperty(propertyName, out var element);

        if (!isPresent || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && DateTime.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedDate))
        {
            value = parsedDate;
            return true;
        }

        error = $"{propertyName} must be a valid ISO 8601 date-time or null.";
        return false;
    }

    private static bool HasOnlyProperties(JsonElement arguments, params string[] allowedNames)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return arguments.EnumerateObject()
            .All(property => allowedNames.Contains(property.Name, StringComparer.Ordinal));
    }

    private static AssistantOutcome InvalidArguments(string action, string message)
    {
        return CreateOutcome(AssistantOutcomeStatus.InvalidArguments, action, message);
    }

    private static AssistantOutcome TaskNotFound(int id)
    {
        return CreateOutcome(
            AssistantOutcomeStatus.NotFound,
            "none",
            $"Task with ID {id} was not found.");
    }

    private static AssistantOutcome CreateOutcome(
        AssistantOutcomeStatus status,
        string action,
        string result,
        object? data = null,
        string? confirmationToken = null)
    {
        return new AssistantOutcome
        {
            Status = status,
            Action = action,
            Result = result,
            Data = data,
            ConfirmationToken = confirmationToken
        };
    }

    private static ListTasksExecution FailedList(AssistantOutcome error)
    {
        return new ListTasksExecution
        {
            Tasks = [],
            Error = error
        };
    }

    private ListTasksExecution RejectedList(string functionName, AssistantOutcome outcome)
    {
        _logger.LogWarning(
            "Rejected proposed function call {FunctionName}: {Reason}",
            functionName,
            outcome.Result);

        return FailedList(outcome);
    }
}
