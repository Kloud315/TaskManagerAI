using TaskManagerAI.Models;

namespace TaskManagerAI.Services;

public class AssistantService
{
    private const int MaximumGeminiTurns = 4;

    private readonly IGeminiClient _geminiClient;
    private readonly ToolExecutor _toolExecutor;

    public AssistantService(IGeminiClient geminiClient, ToolExecutor toolExecutor)
    {
        _geminiClient = geminiClient;
        _toolExecutor = toolExecutor;
    }

    public async Task<AssistantOutcome> HandleMessageAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var conversation = new List<object>
        {
            new
            {
                role = "user",
                parts = new[] { new { text = message } }
            }
        };

        AssistantOutcome? lastListOutcome = null;
        List<TaskItem>? titleMatchesFromLookup = null;

        for (var turnNumber = 0; turnNumber < MaximumGeminiTurns; turnNumber++)
        {
            GeminiTurn turn;
            try
            {
                turn = await _geminiClient.GetNextTurnAsync(conversation, cancellationToken);
            }
            catch (GeminiUnavailableException exception)
            {
                return CreateOutcome(
                    AssistantOutcomeStatus.Unavailable,
                    "none",
                    exception.Message);
            }

            if (turn.ContainsMultipleFunctionCalls)
            {
                return CreateOutcome(
                    AssistantOutcomeStatus.CouldNotComplete,
                    "none",
                    "Please ask for one action at a time.");
            }

            if (turn.FunctionCall is null)
            {
                if (lastListOutcome is not null)
                {
                    return string.IsNullOrWhiteSpace(turn.Text)
                        ? lastListOutcome
                        : CreateOutcome(
                            AssistantOutcomeStatus.Success,
                            "list_tasks",
                            turn.Text,
                            lastListOutcome.Data);
                }

                var responseText = turn.Text.Trim();
                if (responseText.Length > 0
                    && !string.Equals(
                        responseText,
                        "OUT_OF_SCOPE",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return CreateOutcome(
                        AssistantOutcomeStatus.ClarificationNeeded,
                        "none",
                        responseText);
                }

                return CreateOutcome(
                    AssistantOutcomeStatus.OutOfScope,
                    "none",
                    "Sorry, I can only help with managing tasks.");
            }

            var proposal = turn.FunctionCall;
            if (proposal.Name == "list_tasks")
            {
                var listExecution = await _toolExecutor.ExecuteListTasksAsync(
                    proposal.Arguments,
                    cancellationToken);

                if (listExecution.Error is not null)
                {
                    return listExecution.Error;
                }

                if (listExecution.TitleMatches is not null)
                {
                    titleMatchesFromLookup = listExecution.TitleMatches;
                }

                if (IsMutationIntent(message))
                {
                    var titleMatchError = _toolExecutor.ValidateTitleMatchesForMutation(
                        titleMatchesFromLookup);
                    if (titleMatchError is not null)
                    {
                        return titleMatchError;
                    }
                }

                lastListOutcome = CreateOutcome(
                    AssistantOutcomeStatus.Success,
                    "list_tasks",
                    listExecution.Tasks.Count == 0
                        ? "No matching tasks were found."
                        : $"Found {listExecution.Tasks.Count} matching task(s).",
                    listExecution.Tasks);

                conversation.Add(turn.ModelContent);
                conversation.Add(new
                {
                    role = "user",
                    parts = new[]
                    {
                        new
                        {
                            functionResponse = new
                            {
                                name = proposal.Name,
                                response = new { tasks = listExecution.Tasks }
                            }
                        }
                    }
                });

                continue;
            }

            if (titleMatchesFromLookup is null
                && (proposal.Name is "update_task" or "delete_task" or "get_task")
                && !IsProposedIdGroundedInMessage(message, proposal.Arguments))
            {
                return CreateOutcome(
                    AssistantOutcomeStatus.Ambiguous,
                    "none",
                    "I couldn't tell which task you mean. Please give the task ID, or a more specific title.");
            }

            return await _toolExecutor.ExecuteTaskFunctionAsync(
                proposal,
                titleMatchesFromLookup,
                cancellationToken);
        }

        return CreateOutcome(
            AssistantOutcomeStatus.CouldNotComplete,
            "none",
            "I couldn't safely complete that task request. Please try a more specific instruction.");
    }

    private static bool IsMutationIntent(string message)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(
                message,
                @"^\s*(show|list|what|which|find)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            return false;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            message,
            @"\b(delete|remove|mark|complete|update|change|rename|set)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static bool IsProposedIdGroundedInMessage(
        string message,
        System.Text.Json.JsonElement arguments)
    {
        if (arguments.ValueKind != System.Text.Json.JsonValueKind.Object
            || !arguments.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != System.Text.Json.JsonValueKind.Number
            || !idElement.TryGetInt32(out var id))
        {
            return true;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            message,
            $@"(?<!\d){id}(?!\d)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public Task<AssistantOutcome> ConfirmDeleteAsync(
        string confirmationToken,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        return _toolExecutor.ConfirmDeleteAsync(
            confirmationToken,
            confirm,
            cancellationToken);
    }

    private static AssistantOutcome CreateOutcome(
        AssistantOutcomeStatus status,
        string action,
        string result,
        object? data = null)
    {
        return new AssistantOutcome
        {
            Status = status,
            Action = action,
            Result = result,
            Data = data
        };
    }
}
