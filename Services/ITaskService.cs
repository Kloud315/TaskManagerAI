using TaskManagerAI.DTOs;
using TaskManagerAI.Models;

namespace TaskManagerAI.Services;

public interface ITaskService
{
    Task<List<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<TaskItem?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<TaskItem> CreateAsync(
        TaskRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        int id,
        TaskRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default);
}
