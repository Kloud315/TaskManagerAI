using TaskManagerAI.Models;

namespace TaskManagerAI.Data;

public interface ITaskRepository
{
    Task<List<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<TaskItem?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<TaskItem> CreateAsync(
        TaskItem task,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        TaskItem task,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default);
}
