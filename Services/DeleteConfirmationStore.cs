using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TaskManagerAI.Services;

public class DeleteConfirmationStore
{
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, PendingDelete> _pendingDeletes = new();

    public string Create(int taskId, string title)
    {
        RemoveExpiredConfirmations();

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _pendingDeletes[token] = new PendingDelete(
            taskId,
            title,
            DateTimeOffset.UtcNow.Add(ConfirmationLifetime));

        return token;
    }

    public bool TryConsume(string token, out PendingDelete pendingDelete)
    {
        if (_pendingDeletes.TryRemove(token, out var storedDelete)
            && storedDelete.ExpiresAt > DateTimeOffset.UtcNow)
        {
            pendingDelete = storedDelete;
            return true;
        }

        pendingDelete = null!;
        return false;
    }

    private void RemoveExpiredConfirmations()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _pendingDeletes)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _pendingDeletes.TryRemove(entry.Key, out _);
            }
        }
    }

    public sealed record PendingDelete(
        int TaskId,
        string Title,
        DateTimeOffset ExpiresAt);
}
