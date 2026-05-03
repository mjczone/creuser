using Microsoft.Extensions.AI;

namespace Creuser.Scripting.Tests;

/// <summary>
/// Test-only <see cref="IChatClient"/> that emits responses from a queue.
/// One queue entry per <c>GetResponseAsync</c> call. Captures every inbound
/// conversation so a test can assert on the full message history the runner
/// builds across turns.
/// </summary>
internal sealed class StubChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses;
    public List<List<ChatMessage>> Calls { get; } = new();
    public Func<int>? OnResponse { get; set; }

    public StubChatClient(IEnumerable<ChatResponse> responses)
    {
        _responses = new Queue<ChatResponse>(responses);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Calls.Add(messages.ToList());
        OnResponse?.Invoke();
        if (_responses.Count == 0)
            throw new InvalidOperationException("StubChatClient queue is empty.");
        return Task.FromResult(_responses.Dequeue());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("Streaming not supported by StubChatClient.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
