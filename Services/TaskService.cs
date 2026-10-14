using TaskManagerAI.Data;
using TaskManagerAI.DTOs;
using TaskManagerAI.Models;

namespace TaskManagerAI.Services;

public class TaskService : ITaskService
{
    private readonly ITaskRepository _taskRepository;

    public TaskService(ITaskRepository taskRepository)
    {
        _taskRepository = taskRepository;
    }

    public Task<List<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return _taskRepository.GetAllAsync(cancellationToken);
    }

    public Task<TaskItem?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return _taskRepository.GetByIdAsync(id, cancellationToken);
    }

    public Task<TaskItem> CreateAsync(
        TaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var task = new TaskItem
        {
            Title = request.Title,
            IsDone = request.IsDone,
            DueDate = request.DueDate
        };

        return _taskRepository.CreateAsync(task, cancellationToken);
    }

    public Task<bool> UpdateAsync(
        int id,
        TaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var task = new TaskItem
        {
            Id = id,
            Title = request.Title,
            IsDone = request.IsDone,
            DueDate = request.DueDate
        };

        return _taskRepository.UpdateAsync(task, cancellationToken);
    }

    public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        return _taskRepository.DeleteAsync(id, cancellationToken);
    }
}
