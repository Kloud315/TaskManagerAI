using Microsoft.AspNetCore.Mvc;
using TaskManagerAI.DTOs;
using TaskManagerAI.Services;

namespace TaskManagerAI.Controllers;

[ApiController]
[Route("api/assistant")]
public class AssistantController : ControllerBase
{
    private readonly AssistantService _assistantService;

    public AssistantController(AssistantService assistantService)
    {
        _assistantService = assistantService;
    }

    [HttpPost]
    public async Task<IActionResult> Handle(
        AssistantRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await _assistantService.HandleMessageAsync(
            request.Message,
            cancellationToken);

        return ToActionResult(outcome);
    }

    [HttpPost("confirm-delete")]
    public async Task<IActionResult> ConfirmDelete(
        DeleteConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await _assistantService.ConfirmDeleteAsync(
            request.ConfirmationToken,
            request.Confirm,
            cancellationToken);

        return ToActionResult(outcome);
    }

    private IActionResult ToActionResult(AssistantOutcome outcome)
    {
        var response = new AssistantResponse
        {
            Action = outcome.Action,
            Result = outcome.Result,
            Data = outcome.Data,
            ConfirmationToken = outcome.ConfirmationToken
        };

        return outcome.Status switch
        {
            AssistantOutcomeStatus.Success
                or AssistantOutcomeStatus.OutOfScope
                or AssistantOutcomeStatus.ConfirmationRequired
                or AssistantOutcomeStatus.Cancelled => Ok(response),
            AssistantOutcomeStatus.InvalidArguments => BadRequest(response),
            AssistantOutcomeStatus.NotFound => NotFound(response),
            AssistantOutcomeStatus.Ambiguous => Conflict(response),
            AssistantOutcomeStatus.Unavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                response),
            _ => StatusCode(StatusCodes.Status422UnprocessableEntity, response)
        };
    }
}
