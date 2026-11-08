using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddChatClient(new AuditingChatClient(new DemoChatClient()));
builder.Services.AddScoped<WeatherService>();
builder.Services.AddTemporalClient(
    builder.Configuration["TEMPORAL_ADDRESS"] ?? "localhost:7233", "default");

var weather = AIFunctionFactory.Create(
    (string city, IServiceProvider services) =>
        services.GetRequiredService<WeatherService>().GetWeather(city),
    name: "get_weather",
    description: "Gets the weather for a city.");

builder.Services
    .AddHostedTemporalWorker($"middleware-sample-{Guid.NewGuid():N}")
    .AddTemporalAgents(agents =>
    {
        agents.EnableSearchAttributes = false;
        agents.Agent("weather",
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = "Use get_weather for weather questions.",
                    Tools = [weather],
                },
            },
            temporal =>
            {
                temporal.ToolMiddleware = async (agent, context, next, ct) =>
                {
                    Console.WriteLine($"[tool attempt] {agent.Name}/{context.Function.Name}, call={context.CallContent.CallId}");
                    var weatherService = context.Arguments.Services!.GetRequiredService<WeatherService>();
                    Console.WriteLine($"[tool scope] {weatherService.ScopeId}");
                    return await next(context, ct);
                };
            });
    });

using var host = builder.Build();
await host.StartAsync();
try
{
    var proxy = host.Services.GetRequiredKeyedService<AIAgent>("weather");
    var agent = new CallerGuardAgent(proxy);
    var session = (TemporalAgentSession)await agent.CreateSessionAsync();
    var requestPrefix = Guid.NewGuid().ToString("N");
    var first = await agent.RunAsync("Paris", session,
        new TemporalAgentRunOptions { RequestId = $"{requestPrefix}-paris" });
    Console.WriteLine($"First answer: {first.Text}");
    var second = await agent.RunAsync("London", session,
        new TemporalAgentRunOptions { RequestId = $"{requestPrefix}-london" });
    Console.WriteLine($"Second answer: {second.Text}");
    await host.Services.GetRequiredService<ITemporalAgentClient>().ShutdownAsync(session.SessionId);
    Console.WriteLine("Middleware sample complete.");
}
finally
{
    await host.StopAsync();
}

sealed class CallerGuardAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToArray();
        if (snapshot.Length == 0 || snapshot.All(message => string.IsNullOrWhiteSpace(message.Text)))
            throw new ArgumentException("A nonempty request is required.", nameof(messages));
        Console.WriteLine("[caller guard] Runs in the client process, not in workflow history.");
        return base.RunCoreAsync(snapshot, session, options, cancellationToken);
    }
}

sealed class AuditingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[model attempt] Non-streaming transport; retries can repeat this callback.");
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }
}

sealed class WeatherService
{
    public Guid ScopeId { get; } = Guid.NewGuid();
    public string GetWeather(string city) => $"{city}: sunny, 20 degrees C";
}

// A deterministic model stand-in keeps the sample runnable without an API key.
sealed class DemoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = messages.ToArray();
        if (snapshot.Last().Role == ChatRole.Tool)
        {
            var result = snapshot.Last().Contents.OfType<FunctionResultContent>().Single();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Result?.ToString())));
        }
        var city = snapshot.Last(message => message.Role == ChatRole.User).Text!;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"weather-{city}", "get_weather",
                new Dictionary<string, object?> { ["city"] = city })])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
