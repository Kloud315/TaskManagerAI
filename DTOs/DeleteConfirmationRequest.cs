using System.ComponentModel.DataAnnotations;

namespace TaskManagerAI.DTOs;

public class DeleteConfirmationRequest
{
    [Required]
    public string ConfirmationToken { get; set; } = string.Empty;

    public bool Confirm { get; set; }
}
