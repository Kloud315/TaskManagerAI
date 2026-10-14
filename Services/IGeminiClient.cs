namespace TaskManagerAI.Services;

public interface IGeminiClient
{
    Task<GeminiTurn> GetNextTurnAsync(
        IReadOnlyList<object> conversation,
        CancellationToken cancellationToken = default);
}
