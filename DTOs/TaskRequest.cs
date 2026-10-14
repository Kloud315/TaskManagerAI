using System.ComponentModel.DataAnnotations;

namespace TaskManagerAI.DTOs;

public class TaskRequest
{
    [Required]
    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    public bool IsDone { get; set; } = false;

    public DateTime? DueDate { get; set; }
}
