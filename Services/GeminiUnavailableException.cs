namespace TaskManagerAI.Services;

public class GeminiUnavailableException : Exception
{
    public GeminiUnavailableException(string message)
        : base(message)
    {
    }

    public GeminiUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
