using System.ComponentModel.DataAnnotations;

namespace TaskManagerAI.DTOs;

public class AssistantRequest
{
    [Required]
    [MaxLength(2000)]
    public string Message { get; set; } = string.Empty;
}
