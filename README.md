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

   The project also reads the optional `Gemini:Model` setting from configuration. The configured model is `gemini-3.1-flash-lite`. The committed connection string targets LocalDB; for a different SQL Server instance, keep the connection string out of source control and set it in User Secrets:

   ```powershell
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "YOUR_SQL_SERVER_CONNECTION_STRING"
   ```

4. Start the API:

   ```powershell
   dotnet run
   ```

   With the development launch profile, Swagger UI is available at `https://localhost:7230/swagger` or `http://localhost:5057/swagger`.

5. Run the non-destructive assistant smoke test and refresh the recorded responses:

   ```powershell
   .\docs\test-api.ps1
   ```

   The script creates a uniquely named task, tests create/update/not-found assistant flows, and deletes only the task it created. Pass `-LeaveDemoTask` if you want to keep that test task.

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

The project uses Gemini function calling to interpret natural-language task requests and propose calls to the task functions declared by the API. The configured model is `gemini-3.1-flash-lite` (`appsettings.json`), which was confirmed to be available and to return structured function calls during validation. `gemini-2.5-flash` was no longer available to new users.

## Trade-offs and limitations

- Title-based mutations use the model's filtered `list_tasks` results. The server now rejects a clear mutation request if that lookup returns zero or multiple matches, even if the model continues with more tool calls. The model still has to identify a useful title filter.
- Pending deletion tokens are stored in memory, expire after five minutes, and are lost when the API process restarts.
- The assistant depends on the Gemini API. Its natural-language output and function-call choices can vary between runs, and provider failures prevent the task operation from being exercised.
- The recorded run used ADO.NET, as required by the exam, even though the rubric mentions EF Core. The repository uses `SqlConnection` and `SqlCommand` with SQL parameters.

## Error handling

- `ToolExecutor` validates function names, allowed properties, required titles and IDs, value types, date formats, and update fields before calling the task service. Invalid arguments map to HTTP 400; [`CreateTaskWithoutTitle_IsRejected`](./TaskManagerAI.Tests/ToolExecutorTests.cs) verifies a title-less create proposal does not reach task creation.
- A missing task maps to HTTP 404. For clear mutation intents such as delete, update, or mark, title-based operations require a preceding filtered `list_tasks` call; zero matches return HTTP 404 and multiple matches return HTTP 409 before the model can select a task.
- Requests outside the task scope are politely rejected when Gemini returns text without a function call. Gemini unavailability and timeouts map to HTTP 503. The client retries transient HTTP 429/503 responses twice before reporting a provider failure.
- The repository sends values as SQL parameters through ADO.NET; it does not try to sanitize SQL-looking task text.

## Example interactions

The following are actual `/api/assistant` responses recorded in [`docs/test-outputs.txt`](./docs/test-outputs.txt) on **2026-10-15** using `gemini-3.1-flash-lite`. The create and update requests were verified through the CRUD API; the temporary smoke-test task was removed after verification.

### Create a task with a due date

Request: `POST /api/assistant` with `{"message":"Add a task called 'API smoke test 69833939d9d54b49b33e3c30f5b13245' due 2026-10-22"}`

```http
HTTP 200
{"action":"create_task","result":"Created task 16: \"API smoke test 69833939d9d54b49b33e3c30f5b13245\".","data":{"id":16,"title":"API smoke test 69833939d9d54b49b33e3c30f5b13245","isDone":false,"dueDate":"2026-10-22T00:00:00"},"confirmationToken":null}
```

### Mark the task complete

Request: `POST /api/assistant` with `{"message":"Mark task 16 as done"}`

```http
HTTP 200
{"action":"update_task","result":"Updated task 16.","data":{"id":16,"title":"API smoke test 69833939d9d54b49b33e3c30f5b13245","isDone":true,"dueDate":"2026-10-22T00:00:00"},"confirmationToken":null}
```

### Error case: task not found

Request: `POST /api/assistant` with `{"message":"Mark task 2147483647 as done"}`

```http
HTTP 404
{"action":"none","result":"Task with ID 2147483647 was not found.","data":null,"confirmationToken":null}
```

### Error case: out-of-scope question

Request: `POST /api/assistant` with `{"message":"What's the capital of France?"}`

```http
HTTP 200
{"action":"none","result":"Sorry, I can only help with managing tasks.","data":null,"confirmationToken":null}
```

### SQL-looking title remains plain text

The smoke test created and read back a title containing `; DROP TABLE Tasks;--`; the response preserved the exact string and the API continued to operate. The request and response are recorded in [`docs/test-outputs.txt`](./docs/test-outputs.txt).

### Error case: ambiguous title-based deletion

The smoke test creates two tasks sharing a unique title fragment, then requests a deletion by that fragment. The assistant returns HTTP 409 with both matching IDs and does not delete either task. Exact responses are in [`docs/test-outputs.txt`](./docs/test-outputs.txt).

## Short answers

1. **Why shouldn't the AI have database or SQL access?** The model can misunderstand a request, produce unsafe SQL, or be influenced by malicious text. Direct access could expose or change unrelated data. Here it can only propose a small, named task function; C# validates the arguments and the repository uses parameterized SQL.

2. **Which create-task fields are required?** `title` is required because a task without a name is not useful. `isDone` is optional and defaults to `false`; `dueDate` is optional because tasks do not always have a deadline.

3. **What would you add for production?** Store pending delete confirmations in a durable, shared store and audit their creation and use. The current in-memory implementation is single-instance and loses pending confirmations on restart.
