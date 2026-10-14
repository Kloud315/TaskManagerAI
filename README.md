# Task Manager AI

A .NET 8 Web API for managing tasks with regular CRUD endpoints and a natural-language assistant powered by Google's Gemini function calling.

## Requirements

- .NET 8 SDK
- SQL Server LocalDB (or a SQL Server instance)
- A Gemini API key
- `sqlcmd` or SQL Server Management Studio (SSMS) to create the database

## Run locally

1. Open a terminal in the project folder and restore packages:

   ```powershell
   dotnet restore
   ```

2. Create the local database and `Tasks` table. With `sqlcmd` installed, run:

   ```powershell
   sqlcmd -S "(localdb)\MSSQLLocalDB" -d master -i Data\schema.sql
   ```

   Alternatively, open `Data\schema.sql` in SSMS connected to `(localdb)\MSSQLLocalDB` and execute it. The script creates the `TaskManagerAI` database and `dbo.Tasks` table if they do not already exist.

3. Store the Gemini key in .NET User Secrets (replace the example value locally; do not put a real key in source control):

   ```powershell
   dotnet user-secrets set "Gemini:ApiKey" "YOUR_GEMINI_API_KEY"
   ```

   The project also reads the optional `Gemini:Model` setting from configuration. Its default is `gemini-2.5-flash`. The LocalDB connection string is in `appsettings.json`; change it there if using another SQL Server instance.

4. Start the API:

   ```powershell
   dotnet run
   ```

   With the development launch profile, Swagger UI is available at `https://localhost:7230/swagger` or `http://localhost:5057/swagger`.

## API routes

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/tasks` | List all tasks |
| GET | `/api/tasks/{id}` | Get one task by ID |
| POST | `/api/tasks` | Create a task |
| PUT | `/api/tasks/{id}` | Replace a task's title, completion state, and due date |
| DELETE | `/api/tasks/{id}` | Delete a task |
| POST | `/api/assistant` | Ask the assistant to perform a task operation |
| POST | `/api/assistant/confirm-delete` | Confirm or cancel a proposed task deletion |

Task JSON uses `id` (integer), `title` (required, up to 500 characters), `isDone` (boolean, defaults to `false`), and `dueDate` (optional date-time).

Example direct CRUD request:

```powershell
curl.exe -X POST http://localhost:5057/api/tasks `
  -H "Content-Type: application/json" `
  -d '{"title":"Finish report","isDone":false}'
```

Assistant request body:

```json
{
  "message": "Add a task called 'Finish report' due 2027-01-15"
}
```

## Architecture

```text
HTTP request
    |
    v
GlobalExceptionHandlingMiddleware
    |
    +--> TasksController --> ITaskService / TaskService
    |                            |
    |                            v
    |                      ITaskRepository / TaskRepository --> SQL Server
    |
    +--> AssistantController --> AssistantService
                                    |
                                    +--> IGeminiClient / GeminiClient --> Gemini API
                                    |
                                    +--> ToolExecutor --> ITaskService / TaskService
                                    |                         |
                                    |                         v
                                    |                   ITaskRepository / TaskRepository
                                    |
                                    +--> DeleteConfirmationStore (in-memory tokens)
```

`GlobalExceptionHandlingMiddleware` is registered in the HTTP pipeline before controller routes. `TasksController` delegates CRUD operations through `ITaskService` to `ITaskRepository`; `TaskService` and `TaskRepository` provide the registered implementations. The assistant path runs through `AssistantController` and `AssistantService`, which calls `IGeminiClient` (implemented by `GeminiClient`) and `ToolExecutor`.

Gemini receives the user's message and function declarations, and can only propose a function call. It does not execute database operations. `ToolExecutor` validates the proposed function name and arguments and executes supported task operations through `ITaskService`; the repository uses parameterized ADO.NET commands. Title-based update or delete proposals are checked against matches returned by a preceding `list_tasks` operation.

Deletion is a two-step operation: the assistant API can return a short-lived `confirmationToken`, and `POST /api/assistant/confirm-delete` accepts that token with `confirm: true` or `false`. Tokens are single-use, expire after five minutes, and are held in memory; restarting the API clears pending confirmations.

## Why Gemini (and which model)

The project uses Gemini function calling to interpret natural-language task requests and propose calls to the task functions declared by the API. The configured model is `gemini-2.5-flash` (`appsettings.json`); that is also the model recorded with the test outputs below.

## Trade-offs and limitations

- The assistant's title-ambiguity protection depends on Gemini proposing `list_tasks` first. `AssistantService` passes those title matches to `ToolExecutor`, which rejects an operation if multiple tasks matched; it does not independently search task titles for every proposal.
- Pending deletion tokens are stored in memory, expire after five minutes, and are lost when the API process restarts.
- The assistant depends on the Gemini API. Its natural-language output and function-call choices can vary between runs, and provider failures prevent the task operation from being exercised.
- The recorded run used ADO.NET, as required by the exam, even though the rubric mentions EF Core. The repository uses `SqlConnection` and `SqlCommand` with SQL parameters.

## Error handling

- If Gemini returns no function call, `AssistantService` returns an out-of-scope response. This behavior was not observed in the recorded run because the Gemini requests failed.
- `ToolExecutor` checks proposed function arguments, including required titles, IDs, types, dates, and supported fields. The API maps invalid-argument outcomes to HTTP 400. The recorded missing-title requests failed with HTTP 503 before this validation path could be observed; the [`CreateTaskWithoutTitle_IsRejected` unit test](./TaskManagerAI.Tests/ToolExecutorTests.cs) asserts that a title-less create proposal is rejected without calling task creation.
- A missing task is represented as a not-found outcome, and ambiguous matches as a conflict outcome. Neither outcome was observed through the assistant in the recorded run because Gemini returned errors.
- A Gemini provider failure is returned by the assistant endpoint as HTTP 503. The recorded responses reported Gemini HTTP 404 for the initial run and HTTP 400 for case 5.
- The repository passes task values as SQL parameters. The SQL-looking-title request did not create a task in the recorded run, so that run does not demonstrate storage of the supplied title.

## Example interactions

The following are the exact requests and actual responses recorded in [`docs/test-outputs.txt`](./docs/test-outputs.txt). The initial run and database reset were recorded on **2026-10-15**; case 5 was recorded later that day. LLM output and function-call choices can vary between runs. In this run, Gemini errors prevented the assistant cases from completing; the attempted happy paths below are not successful examples.

### Happy path 1: create a task due next Friday

Request: `POST /api/assistant` — `Add a task called 'Call dentist' due next Friday`

```http
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}
```

The request did not create a task because the assistant reported Gemini HTTP 404; today was 2026-10-15 and next Friday was 2026-10-16, but no API due date was returned to verify.

### Happy path 2: mark task 1 done

Request: `POST /api/assistant` — `Mark task 1 as done`

```http
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}
```

The task was not marked done because the request failed at the Gemini call, and the preceding create request had also failed.

### Happy path 3: list tasks that are not done

Request: `POST /api/assistant` — `Show me everything that's not done yet`

```http
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}
```

The assistant did not return a task list because Gemini returned HTTP 404.

### Error case 1: out-of-scope request

Request: `POST /api/assistant` — `What's the capital of France?`

```http
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}
```

The request did not reach out-of-scope handling because Gemini returned HTTP 404.

### Error case 2: task not found

Request: `POST /api/assistant` — `Mark task 999 as done`

```http
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}
```

The API did not evaluate whether task 999 exists because Gemini returned HTTP 404.

### Error case 3: ambiguous delete

Requests:

- `POST /api/assistant` — `Add a task called 'Finish report'`
- `POST /api/assistant` — `Add a task called 'Review report'`
- `POST /api/assistant` — `Delete the report task`
- `GET /api/tasks`

```http
POST /api/assistant — create Finish report
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}

POST /api/assistant — create Review report
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}

POST /api/assistant — delete the report task
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}

GET /api/tasks
HTTP 200
[]
```

Both setup requests and the assistant delete request failed with Gemini HTTP 404, so ambiguity was not exercised; the subsequent task list was empty.

### Error case 4: missing title

Requests:

- `POST /api/assistant` — `Add a task`
- `POST /api/assistant` — `Create a task with no name`
- `POST /api/assistant` — `Add a task with no title`

```http
Each of the three requests:
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}
```

Gemini did not ask for a title in text, and none of these requests returned HTTP 400: all failed with HTTP 503 before the validation result could be observed. The [`CreateTaskWithoutTitle_IsRejected` unit test](./TaskManagerAI.Tests/ToolExecutorTests.cs) asserts that `ToolExecutor` rejects a create proposal without a title and does not call task creation.

### Error case 5: invalid Gemini API key

Request: `POST /api/assistant` — `Show me all tasks`

```http
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 400.","data":null,"confirmationToken":null}
```

The assistant returned HTTP 503 with Gemini HTTP 400; the recorded response does not include a more specific provider error description.

### Error case 6: SQL-looking task title

Requests:

- `POST /api/assistant` — `Add a task called '); DROP TABLE Tasks;--`
- `GET /api/tasks`

```http
POST /api/assistant
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}

GET /api/tasks
HTTP 200
[]
```

The task was not created because Gemini returned HTTP 404; the later successful list request shows the endpoint remained available but does not prove that this title was stored as plain text.

### Delete confirmation flow

Requests:

- `POST /api/assistant` — `Add a task called 'Temporary delete confirmation target'`
- `POST /api/assistant` — `Delete task called 'Temporary delete confirmation target'`
- Confirm using the returned token

```http
Create target:
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}

Request deletion:
HTTP 503
{"action":"none","result":"Gemini returned HTTP status 404.","data":null}

Confirm with token:
NOT RUN
The preceding API response did not contain a confirmationToken.
```

No delete token was returned, so confirmation and token reuse could not be tested. The cancellation attempt was also blocked: creating `Keep this task after cancellation` and requesting its deletion each returned HTTP 503 with Gemini HTTP 404, and the `confirm: false` request was recorded as `NOT RUN` because no token was available.

## Short answers

1. **Why shouldn't the AI have database or SQL access?** The model can misunderstand a request, produce unsafe SQL, or be influenced by malicious text. Direct access could expose or change unrelated data. Here it can only propose a small, named task function; C# validates the arguments and the repository uses parameterized SQL.

2. **Which create-task fields are required?** `title` is required because a task without a name is not useful. `isDone` is optional and defaults to `false`; `dueDate` is optional because tasks do not always have a deadline.

3. **What would you add for production?** Require explicit user confirmation before deleting a task. That creates a deliberate checkpoint for an irreversible action, even if the model misunderstood the request.
