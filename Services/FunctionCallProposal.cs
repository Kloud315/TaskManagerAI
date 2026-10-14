using System.Text.Json;

namespace TaskManagerAI.Services;

public class FunctionCallProposal
{
    public required string Name { get; init; }

    public required JsonElement Arguments { get; init; }
}
