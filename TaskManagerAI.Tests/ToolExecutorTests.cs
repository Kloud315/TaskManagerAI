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

    private sealed class FakeTaskService : ITaskService
    {
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int GetByIdCalls { get; private set; }
        public int? LastRequestedId { get; private set; }
        public TaskItem? ExistingTask { get; init; }

        public Task<List<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<TaskItem>());
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
