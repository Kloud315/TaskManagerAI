using System.Text.Json;

namespace TaskManagerAI.Services;

public sealed record GeminiTurn(
    FunctionCallProposal? FunctionCall,
    JsonElement ModelContent,
    string Text)
{
    public bool ContainsMultipleFunctionCalls { get; init; }
}
