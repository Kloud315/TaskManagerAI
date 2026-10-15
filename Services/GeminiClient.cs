using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskManagerAI.Configuration;

namespace TaskManagerAI.Services;

public class GeminiClient : IGeminiClient
{
    private const int MaximumTransientRetries = 2;
    private const string GeminiApiBaseUrl =
        "https://generativelanguage.googleapis.com/v1beta/models/";

    private const string SystemPrompt = """
        You help users manage tasks. Use only the declared task functions.
        If a task is referred to by title rather than by ID, call list_tasks to find matches
        before choosing an ID. If multiple tasks match a request to change one task, ask the
        user to specify the ID. Do not guess task IDs or claim a task was changed unless the
        matching function was called. If the request is unrelated to tasks, say so briefly.
        """;

    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiClient> _logger;

    public GeminiClient(
        HttpClient httpClient,
        IOptions<GeminiOptions> options,
        ILogger<GeminiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GeminiTurn> GetNextTurnAsync(
        IReadOnlyList<object> conversation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new GeminiUnavailableException(
                "The Gemini API key is not configured. Add it with .NET User Secrets.");
        }

        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            throw new GeminiUnavailableException("The Gemini model is not configured.");
        }

        var requestBody = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = $"Today's date is {DateTime.Today.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture)}.\n{SystemPrompt}"
                    }
                }
            },
            contents = conversation,
            tools = new[]
            {
                new
                {
                    functionDeclarations = GetFunctionDeclarations()
                }
            }
        };

        try
        {
            var model = Uri.EscapeDataString(_options.Model);
            for (var attempt = 0; attempt <= MaximumTransientRetries; attempt++)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{GeminiApiBaseUrl}{model}:generateContent");
                request.Headers.Add("x-goog-api-key", _options.ApiKey);
                request.Content = JsonContent.Create(requestBody);

                var shouldRetry = false;
                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                        return ParseGeminiTurn(responseJson);
                    }

                    if ((response.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                        or System.Net.HttpStatusCode.ServiceUnavailable)
                        && attempt < MaximumTransientRetries)
                    {
                        _logger.LogWarning(
                            "Gemini returned transient HTTP status {StatusCode}; retrying ({RetryNumber}/{MaximumRetries}).",
                            (int)response.StatusCode,
                            attempt + 1,
                            MaximumTransientRetries);
                        shouldRetry = true;
                    }
                    else
                    {
                        throw new GeminiUnavailableException(
                            $"Gemini returned HTTP status {(int)response.StatusCode}.");
                    }
                }

                if (shouldRetry)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(attempt + 1),
                        cancellationToken);
                }
            }

            throw new GeminiUnavailableException(
                "Gemini remained unavailable after transient retries.");
        }
        catch (HttpRequestException exception)
        {
            throw new GeminiUnavailableException(
                "Gemini could not be reached. Please try again later.",
                exception);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeminiUnavailableException(
                "Gemini timed out. Please try again later.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new GeminiUnavailableException(
                "Gemini returned a response that could not be read.",
                exception);
        }
    }

    private GeminiTurn ParseGeminiTurn(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
        {
            throw new GeminiUnavailableException("Gemini returned no response candidates.");
        }

        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object
            || !candidate.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
        {
            throw new GeminiUnavailableException("Gemini returned an incomplete response.");
        }

        FunctionCallProposal? proposal = null;
        var textParts = new List<string>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (part.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                textParts.Add(textElement.GetString()!);
            }

            if (!part.TryGetProperty("functionCall", out var functionCall))
            {
                continue;
            }

            _logger.LogInformation(
                "Gemini proposed function call: {FunctionCall}",
                functionCall.GetRawText());

            if (proposal is not null)
            {
                throw new GeminiUnavailableException(
                    "Gemini proposed multiple functions in one response. Please try again.");
            }

            if (functionCall.ValueKind != JsonValueKind.Object
                || !functionCall.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                throw new GeminiUnavailableException(
                    "Gemini returned a function call without a valid name.");
            }

            var arguments = functionCall.TryGetProperty("args", out var args)
                ? args.Clone()
                : EmptyJsonObject();

            proposal = new FunctionCallProposal
            {
                Name = nameElement.GetString()!,
                Arguments = arguments
            };
        }

        return new GeminiTurn(
            proposal,
            content.Clone(),
            string.Join(Environment.NewLine, textParts));
    }

    private static JsonElement EmptyJsonObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static object[] GetFunctionDeclarations()
    {
        return
        [
            new
            {
                name = "create_task",
                description = "Create a task. The title is required.",
                parameters = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        title = new { type = "STRING", description = "The task title." },
                        isDone = new { type = "BOOLEAN", description = "Whether the task is complete." },
                        dueDate = new { type = "STRING", description = "Optional due date in ISO 8601 format." }
                    },
                    required = new[] { "title" }
                }
            },
            new
            {
                name = "update_task",
                description = "Update one task by ID. Provide at least one field to change.",
                parameters = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        id = new { type = "INTEGER", description = "The task ID." },
                        title = new { type = "STRING", description = "The replacement task title." },
                        isDone = new { type = "BOOLEAN", description = "Whether the task is complete." },
                        dueDate = new { type = "STRING", description = "The new due date in ISO 8601 format." }
                    },
                    required = new[] { "id" }
                }
            },
            new
            {
                name = "delete_task",
                description = "Delete one task by ID.",
                parameters = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        id = new { type = "INTEGER", description = "The task ID." }
                    },
                    required = new[] { "id" }
                }
            },
            new
            {
                name = "list_tasks",
                description = "Find tasks, optionally filtering by part of the title or completion status.",
                parameters = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        title_contains = new { type = "STRING", description = "Optional part of a task title." },
                        is_done = new { type = "BOOLEAN", description = "Optional completion status filter." }
                    }
                }
            },
            new
            {
                name = "get_task",
                description = "Get one task by ID.",
                parameters = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        id = new { type = "INTEGER", description = "The task ID." }
                    },
                    required = new[] { "id" }
                }
            }
        ];
    }
}
