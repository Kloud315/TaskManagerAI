using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskManagerAI.Configuration;
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
    public async Task AssistantRejectsUngroundedDeleteIdWithoutTitleLookup()
    {
        using var arguments = JsonDocument.Parse("""{"id":1}""");
        using var modelContent = JsonDocument.Parse("""{"role":"model"}""");
        var taskService = new FakeTaskService
        {
            ExistingTasks =
            [
                new TaskItem { Id = 1, Title = "Finish report" },
                new TaskItem { Id = 2, Title = "Review report" }
            ]
        };
        var assistant = new AssistantService(
            new FakeGeminiClient(new GeminiTurn(
                CreateProposal("delete_task", arguments),
                modelContent.RootElement.Clone(),
                string.Empty)),
            CreateExecutor(taskService));

        var outcome = await assistant.HandleMessageAsync("Delete the report task");

        Assert.Equal(AssistantOutcomeStatus.Ambiguous, outcome.Status);
        Assert.Equal("none", outcome.Action);
        Assert.Equal(
            "I couldn't tell which task you mean. Please give the task ID, or a more specific title.",
            outcome.Result);
        Assert.Null(outcome.ConfirmationToken);
        Assert.Equal(0, taskService.GetByIdCalls);
        Assert.Equal(0, taskService.DeleteCalls);
    }

    [Fact]
    public async Task AssistantExecutesUpdateWhenIdAppearsInMessage()
    {
        using var arguments = JsonDocument.Parse("""{"id":3,"isDone":true}""");
        using var modelContent = JsonDocument.Parse("""{"role":"model"}""");
        var taskService = new FakeTaskService
        {
            ExistingTask = new TaskItem { Id = 3, Title = "Finish report" }
        };
        var assistant = new AssistantService(
            new FakeGeminiClient(new GeminiTurn(
                CreateProposal("update_task", arguments),
                modelContent.RootElement.Clone(),
                string.Empty)),
            CreateExecutor(taskService));

        var outcome = await assistant.HandleMessageAsync("Mark task 3 as done");

        Assert.Equal(AssistantOutcomeStatus.Success, outcome.Status);
        Assert.Equal("update_task", outcome.Action);
        Assert.Equal(3, taskService.LastRequestedId);
        Assert.Equal(1, taskService.UpdateCalls);
    }

    [Fact]
    public async Task AssistantProposesDeleteWhenIdAppearsInMessage()
    {
        using var arguments = JsonDocument.Parse("""{"id":3}""");
        using var modelContent = JsonDocument.Parse("""{"role":"model"}""");
        var taskService = new FakeTaskService
        {
            ExistingTask = new TaskItem { Id = 3, Title = "Finish report" }
        };
        var assistant = new AssistantService(
            new FakeGeminiClient(new GeminiTurn(
                CreateProposal("delete_task", arguments),
                modelContent.RootElement.Clone(),
                string.Empty)),
            CreateExecutor(taskService));

        var outcome = await assistant.HandleMessageAsync("Delete task 3");

        Assert.Equal(AssistantOutcomeStatus.ConfirmationRequired, outcome.Status);
        Assert.NotNull(outcome.ConfirmationToken);
        Assert.Equal(0, taskService.DeleteCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  out_of_scope  ")]
    [InlineData("Out_Of_Scope")]
    public async Task AssistantMapsEmptyOrOutOfScopeTextToOutOfScope(
        string responseText)
    {
        using var modelContent = JsonDocument.Parse("""{"role":"model"}""");
        var geminiClient = new FakeGeminiClient(new GeminiTurn(
            null,
            modelContent.RootElement.Clone(),
            responseText));
        var assistant = new AssistantService(
            geminiClient,
            CreateExecutor(new FakeTaskService()));

        var outcome = await assistant.HandleMessageAsync("What is the weather?");

        Assert.Equal(AssistantOutcomeStatus.OutOfScope, outcome.Status);
        Assert.Equal("none", outcome.Action);
        Assert.Equal("Sorry, I can only help with managing tasks.", outcome.Result);
    }

    [Fact]
    public async Task AssistantReturnsModelClarifyingQuestion()
    {
        using var modelContent = JsonDocument.Parse("""{"role":"model"}""");
        const string question = "What should the task title be?";
        var geminiClient = new FakeGeminiClient(new GeminiTurn(
            null,
            modelContent.RootElement.Clone(),
            question));
        var assistant = new AssistantService(
            geminiClient,
            CreateExecutor(new FakeTaskService()));

        var outcome = await assistant.HandleMessageAsync("Add a task");

        Assert.Equal(AssistantOutcomeStatus.ClarificationNeeded, outcome.Status);
        Assert.Equal("none", outcome.Action);
        Assert.Equal(question, outcome.Result);
    }

    [Fact]
    public async Task AssistantDoesNotExecuteMultipleProposedFunctions()
    {
        const string responseBody = """
            {"candidates":[{"content":{"role":"model","parts":[
              {"functionCall":{"name":"create_task","args":{"title":"first"}}},
              {"functionCall":{"name":"list_tasks","args":{}}}
            ]}}]}
            """;
        var taskService = new FakeTaskService();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(responseBody));
        var geminiClient = new GeminiClient(
            httpClient,
            Options.Create(new GeminiOptions
            {
                ApiKey = "test-key",
                Model = "test-model"
            }),
            NullLogger<GeminiClient>.Instance);
        var assistant = new AssistantService(
            geminiClient,
            CreateExecutor(taskService));

        var outcome = await assistant.HandleMessageAsync("Add a task and list my tasks");

        Assert.Equal(AssistantOutcomeStatus.CouldNotComplete, outcome.Status);
        Assert.Equal("none", outcome.Action);
        Assert.Equal("Please ask for one action at a time.", outcome.Result);
        Assert.Equal(0, taskService.CreateCalls);
        Assert.Equal(0, taskService.GetAllCalls);
        Assert.Equal(0, taskService.GetByIdCalls);
        Assert.Equal(0, taskService.DeleteCalls);
    }

    [Fact]
    public async Task ListingQuestionContainingMutationWordIsNotRejectedAsMutation()
    {
        using var listArguments = JsonDocument.Parse(
            """{"title_contains":"update","is_done":false}""");
        using var modelContent = JsonDocument.Parse("""{"role":"model"}""");
        var taskService = new FakeTaskService();
        var geminiClient = new FakeGeminiClient(
            new GeminiTurn(
                CreateProposal("list_tasks", listArguments),
                modelContent.RootElement.Clone(),
                string.Empty),
            new GeminiTurn(
                null,
                modelContent.RootElement.Clone(),
                "Here are the incomplete tasks matching your request."));
        var assistant = new AssistantService(
            geminiClient,
            CreateExecutor(taskService));

        var outcome = await assistant.HandleMessageAsync(
            "What tasks do I still need to update?");

        Assert.Equal(AssistantOutcomeStatus.Success, outcome.Status);
        Assert.Equal("list_tasks", outcome.Action);
        Assert.Equal(2, geminiClient.CallCount);
        Assert.Empty(taskService.ExistingTasks);
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
            private readonly GeminiTurn[] _turns;

            public FakeGeminiClient(params GeminiTurn[] turns)
            {
                _turns = turns;
            }

            public int CallCount { get; private set; }

            public Task<GeminiTurn> GetNextTurnAsync(
                IReadOnlyList<object> conversation,
                CancellationToken cancellationToken = default)
            {
                CallCount++;
                return Task.FromResult(_turns[Math.Min(CallCount - 1, _turns.Length - 1)]);
            }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public StubHttpMessageHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responseBody,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class FakeTaskService : ITaskService
    {
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int GetAllCalls { get; private set; }
        public int GetByIdCalls { get; private set; }
        public int? LastRequestedId { get; private set; }
        public TaskItem? ExistingTask { get; init; }
        public List<TaskItem> ExistingTasks { get; init; } = [];

        public Task<List<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            GetAllCalls++;
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
            return Task.FromResult(ExistingTask?.Id == id);
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
