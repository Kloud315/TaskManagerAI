using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TaskManagerAI.DTOs;
using TaskManagerAI.Models;
using TaskManagerAI.Services;

namespace TaskManagerAI.Tests;

public class ToolExecutorTests
{
    [Fact]
    public async Task CreateTaskWithoutTitle_IsRejected()
    {
        var taskService = new FakeTaskService();
        var executor = CreateExecutor(taskService);
        using var arguments = JsonDocument.Parse("""{"isDone":false}""");

        var outcome = await executor.ExecuteTaskFunctionAsync(
            CreateProposal("create_task", arguments),
            titleMatches: null);

        Assert.Equal(AssistantOutcomeStatus.InvalidArguments, outcome.Status);
        Assert.Contains("title", outcome.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, taskService.CreateCalls);
    }

    [Fact]
    public async Task UpdateTaskForMissingId_ReturnsNotFound()
    {
        var taskService = new FakeTaskService();
        var executor = CreateExecutor(taskService);
        using var arguments = JsonDocument.Parse("""{"id":404,"isDone":true}""");

        var outcome = await executor.ExecuteTaskFunctionAsync(
            CreateProposal("update_task", arguments),
            titleMatches: null);

        Assert.Equal(AssistantOutcomeStatus.NotFound, outcome.Status);
        Assert.Contains("404", outcome.Result);
        Assert.Equal(404, taskService.LastRequestedId);
        Assert.Equal(0, taskService.UpdateCalls);
    }

    [Fact]
    public async Task DeleteTaskWithAmbiguousTitleMatch_IsRejected()
    {
        var taskService = new FakeTaskService();
        var executor = CreateExecutor(taskService);
        using var arguments = JsonDocument.Parse("""{"id":1}""");
        var matchingTasks = new List<TaskItem>
        {
            new() { Id = 1, Title = "Finish report" },
            new() { Id = 2, Title = "Review report" }
        };

        var outcome = await executor.ExecuteTaskFunctionAsync(
            CreateProposal("delete_task", arguments),
            matchingTasks);

        Assert.Equal(AssistantOutcomeStatus.Ambiguous, outcome.Status);
        Assert.Contains("multiple matching tasks", outcome.Result);
        Assert.Equal(0, taskService.DeleteCalls);
        Assert.Equal(0, taskService.GetByIdCalls);
    }

    [Fact]
    public void ValidateTitleMatchesForMutation_WithMultipleMatches_ReturnsAmbiguous()
    {
        var executor = CreateExecutor(new FakeTaskService());
        var matches = new List<TaskItem>
        {
            new() { Id = 1, Title = "Finish report" },
            new() { Id = 2, Title = "Review report" }
        };

        var outcome = executor.ValidateTitleMatchesForMutation(matches);

        Assert.Equal(AssistantOutcomeStatus.Ambiguous, outcome?.Status);
        Assert.Contains("multiple matching tasks", outcome!.Result);
    }

    [Fact]
    public void ValidateTitleMatchesForMutation_WithNoMatches_ReturnsNotFound()
    {
        var executor = CreateExecutor(new FakeTaskService());

        var outcome = executor.ValidateTitleMatchesForMutation([]);

        Assert.Equal(AssistantOutcomeStatus.NotFound, outcome?.Status);
        Assert.Contains("No task matched", outcome!.Result);
    }

    [Fact]
    public void ValidateTitleMatchesForMutation_WithOneMatchAllowsExecution()
    {
        var executor = CreateExecutor(new FakeTaskService());

        var outcome = executor.ValidateTitleMatchesForMutation(
        [
            new TaskItem { Id = 1, Title = "Finish report" }
        ]);

        Assert.Null(outcome);
    }

    [Fact]
    public async Task AssistantStopsBeforeMutationWhenTitleLookupIsAmbiguous()
    {
        using var arguments = JsonDocument.Parse("""{"title_contains":"report"}""");
        using var modelContent = JsonDocument.Parse("""{"role":"model"}""");
        var taskService = new FakeTaskService
        {
            ExistingTasks =
            [
                new TaskItem { Id = 1, Title = "Finish report" },
                new TaskItem { Id = 2, Title = "Review report" }
            ]
        };
        var geminiClient = new FakeGeminiClient(new GeminiTurn(
            CreateProposal("list_tasks", arguments),
            modelContent.RootElement.Clone(),
            string.Empty));
        var assistant = new AssistantService(
            geminiClient,
            CreateExecutor(taskService));

        var outcome = await assistant.HandleMessageAsync("Delete the report task");

        Assert.Equal(AssistantOutcomeStatus.Ambiguous, outcome.Status);
        Assert.Equal(1, geminiClient.CallCount);
        Assert.Equal(0, taskService.DeleteCalls);
    }

    [Fact]
    public async Task DeleteTask_RequiresConfirmationBeforeDeleting()
    {
        var taskService = new FakeTaskService
        {
            ExistingTask = new TaskItem { Id = 1, Title = "Finish report" }
        };
        var executor = CreateExecutor(taskService);
        using var arguments = JsonDocument.Parse("""{"id":1}""");

        var pendingOutcome = await executor.ExecuteTaskFunctionAsync(
            CreateProposal("delete_task", arguments),
            titleMatches: null);

        Assert.Equal(AssistantOutcomeStatus.ConfirmationRequired, pendingOutcome.Status);
        Assert.NotNull(pendingOutcome.ConfirmationToken);
        Assert.Equal(0, taskService.DeleteCalls);

        var confirmedOutcome = await executor.ConfirmDeleteAsync(
            pendingOutcome.ConfirmationToken!,
            confirm: true);

        Assert.Equal(AssistantOutcomeStatus.Success, confirmedOutcome.Status);
        Assert.Equal(1, taskService.DeleteCalls);
    }

    private static FunctionCallProposal CreateProposal(
        string name,
        JsonDocument arguments)
    {
        return new FunctionCallProposal
        {
            Name = name,
            Arguments = arguments.RootElement.Clone()
        };
    }

    private static ToolExecutor CreateExecutor(FakeTaskService taskService)
    {
        return new ToolExecutor(
            taskService,
            NullLogger<ToolExecutor>.Instance,
            new DeleteConfirmationStore());
    }

    private sealed class FakeGeminiClient : IGeminiClient
    {
            private readonly GeminiTurn _turn;

            public FakeGeminiClient(GeminiTurn turn)
            {
                _turn = turn;
            }

            public int CallCount { get; private set; }

            public Task<GeminiTurn> GetNextTurnAsync(
                IReadOnlyList<object> conversation,
                CancellationToken cancellationToken = default)
            {
                CallCount++;
                return Task.FromResult(_turn);
            }
    }

    private sealed class FakeTaskService : ITaskService
    {
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int GetByIdCalls { get; private set; }
        public int? LastRequestedId { get; private set; }
        public TaskItem? ExistingTask { get; init; }
        public List<TaskItem> ExistingTasks { get; init; } = [];

        public Task<List<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ExistingTasks.ToList());
        }

        public Task<TaskItem?> GetByIdAsync(
            int id,
            CancellationToken cancellationToken = default)
        {
            GetByIdCalls++;
            LastRequestedId = id;
            return Task.FromResult(
                ExistingTask?.Id == id ? ExistingTask : null);
        }

        public Task<TaskItem> CreateAsync(
            TaskRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(new TaskItem { Id = 1, Title = request.Title });
        }

        public Task<bool> UpdateAsync(
            int id,
            TaskRequest request,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            return Task.FromResult(false);
        }

        public Task<bool> DeleteAsync(
            int id,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(ExistingTask?.Id == id);
        }
    }
}
