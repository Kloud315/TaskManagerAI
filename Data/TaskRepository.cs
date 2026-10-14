using System.Data;
using Microsoft.Data.SqlClient;
using TaskManagerAI.Models;

namespace TaskManagerAI.Data;

public class TaskRepository : ITaskRepository
{
    private readonly string _connectionString;

    public TaskRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Title, IsDone, DueDate
            FROM dbo.Tasks
            ORDER BY Id;
            """;

        using (var connection = new SqlConnection(_connectionString))
        using (var command = new SqlCommand(sql, connection))
        {
            await connection.OpenAsync(cancellationToken);

            var tasks = new List<TaskItem>();

            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    tasks.Add(ReadTask(reader));
                }
            }

            return tasks;
        }
    }

    public async Task<TaskItem?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Title, IsDone, DueDate
            FROM dbo.Tasks
            WHERE Id = @Id;
            """;

        using (var connection = new SqlConnection(_connectionString))
        using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            await connection.OpenAsync(cancellationToken);

            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    return ReadTask(reader);
                }
            }
        }

        return null;
    }

    public async Task<TaskItem> CreateAsync(
        TaskItem task,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.Tasks (Title, IsDone, DueDate)
            OUTPUT INSERTED.Id, INSERTED.Title, INSERTED.IsDone, INSERTED.DueDate
            VALUES (@Title, @IsDone, @DueDate);
            """;

        using (var connection = new SqlConnection(_connectionString))
        using (var command = new SqlCommand(sql, connection))
        {
            AddTaskParameters(command, task);
            await connection.OpenAsync(cancellationToken);

            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The inserted task could not be read back from the database.");
                }

                return ReadTask(reader);
            }
        }
    }

    public async Task<bool> UpdateAsync(
        TaskItem task,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.Tasks
            SET Title = @Title,
                IsDone = @IsDone,
                DueDate = @DueDate
            WHERE Id = @Id;
            """;

        using (var connection = new SqlConnection(_connectionString))
        using (var command = new SqlCommand(sql, connection))
        {
            AddTaskParameters(command, task);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = task.Id;
            await connection.OpenAsync(cancellationToken);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM dbo.Tasks WHERE Id = @Id;";

        using (var connection = new SqlConnection(_connectionString))
        using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            await connection.OpenAsync(cancellationToken);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
    }

    private static void AddTaskParameters(SqlCommand command, TaskItem task)
    {
        command.Parameters.Add("@Title", SqlDbType.NVarChar, 500).Value = task.Title;
        command.Parameters.Add("@IsDone", SqlDbType.Bit).Value = task.IsDone;
        command.Parameters.Add("@DueDate", SqlDbType.DateTime2).Value =
            task.DueDate.HasValue ? task.DueDate.Value : DBNull.Value;
    }

    private static TaskItem ReadTask(SqlDataReader reader)
    {
        return new TaskItem
        {
            Id = reader.GetInt32(0),
            Title = reader.GetString(1),
            IsDone = reader.GetBoolean(2),
            DueDate = reader.IsDBNull(3) ? null : reader.GetDateTime(3)
        };
    }
}
