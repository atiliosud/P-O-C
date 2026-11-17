namespace CaseAgents.Agents;

/// <summary>
/// Holds the HttpClient the AI backend uses to call the Case API.
/// Built lazily over SocketsHttpHandler; tests replace the handler with the test server's handler.
/// </summary>
public sealed class CaseAppHttpClientProvider(AiBackendOptions options)
{
    private readonly object _gate = new();
    private HttpClient? _client;

    public HttpClient Client
    {
        get
        {
            lock (_gate)
            {
                _client ??= Create(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
                return _client;
            }
        }
    }

    /// <summary>Replaces the underlying handler (used by tests to route calls to an in-process server).</summary>
    public void UseHandler(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _client = Create(handler);
        }
    }

    private HttpClient Create(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri(options.CaseApiBaseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(30),
    };
}
