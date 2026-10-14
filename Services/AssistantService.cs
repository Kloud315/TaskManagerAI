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
        ListTasksExecution? lastListExecution = null;

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

                lastListExecution = listExecution;
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

            return await _toolExecutor.ExecuteTaskFunctionAsync(
                proposal,
                lastListExecution?.TitleMatches,
                cancellationToken);
        }

        return CreateOutcome(
            AssistantOutcomeStatus.CouldNotComplete,
            "none",
            "I couldn't safely complete that task request. Please try a more specific instruction.");
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
