// ChatHistoryMemory — session-scoped MAF episodic memory backed by an in-memory vector store.
//
// Run: dotnet run --project samples/MAF/ChatHistoryMemory/ChatHistoryMemory.csproj

using System.ClientModel;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using OpenAI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;

const int vectorDimensions = 1536;
const string collectionName = "temporal-agent-chat-history";
const string agentName = "MemoryAssistant";
var useOnDemandRecall = args.Contains("--on-demand", StringComparer.OrdinalIgnoreCase);
var hostArgs = args.Where(argument =>
    !string.Equals(argument, "--on-demand", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = Host.CreateApplicationBuilder(hostArgs);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
if (string.IsNullOrWhiteSpace(apiKey))
{
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"...\" " +
        "--project samples/MAF/ChatHistoryMemory");
}

if (string.IsNullOrWhiteSpace(apiBaseUrl))
{
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");
}

var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";
var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

builder.Services.AddChatClient(openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient());
builder.Services.AddSingleton<VectorStore>(_ =>
    new InMemoryVectorStore(new InMemoryVectorStoreOptions
    {
        EmbeddingGenerator = new DeterministicEmbeddingGenerator(),
    }));
builder.Services.AddTemporalClient(temporalAddress, "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(agents =>
        agents.Agent(
            agentName,
            new ChatClientAgentOptions
            {
                Description = "An assistant that can recall earlier conversation details.",
                ChatOptions = new ChatOptions
                {
                    Instructions = useOnDemandRecall
                        ? "You are a helpful assistant. When earlier conversation may answer a " +
                          "question, use the Search function to retrieve it. Say when you do not know."
                        : "You are a helpful assistant. Use recalled conversation details to " +
                          "answer follow-up questions, and say when you do not know.",
                },
            },
            temporal => temporal.UseChatHistoryMemory(memory =>
            {
                memory.UseVectorStore(
                    services => services.GetRequiredService<VectorStore>(),
                    collectionName,
                    vectorDimensions);
                memory.WithScopeInitializer((_, context, _) =>
                {
                    var storageScope = new ChatHistoryMemoryProviderScope
                    {
                        ApplicationId = "chat-history-memory-sample",
                        AgentId = context.AgentName,
                        SessionId = context.SessionId,
                    };
                    return Task.FromResult(
                        new ChatHistoryMemoryProvider.State(storageScope));
                });
                memory.WithRecall(options =>
                {
                    options.SearchTime = useOnDemandRecall
                        ? ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling
                        : ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke;
                    options.MaxResults = 3;
                });
            })));

var host = builder.Build();
await host.StartAsync();
try
{
    var proxy = host.Services.GetTemporalAgentProxy(agentName);
    var session = await proxy.CreateSessionAsync();

    Console.WriteLine("Worker started. This sample stores vectors only in this process.");
    Console.WriteLine(
        $"Recall mode: {(useOnDemandRecall ? "on-demand Search tool" : "automatic BeforeAIInvoke (default)")}");
    Console.WriteLine("Tell the assistant a preference, then ask about it in a later turn.\n");
    Console.Write("User: ");
    var firstMessage = Console.ReadLine();
    if (!string.IsNullOrWhiteSpace(firstMessage))
    {
        var firstResponse = await proxy.RunAsync(firstMessage, session);
        Console.WriteLine($"Agent: {firstResponse.Text}\n");
    }

    Console.Write("User: ");
    var secondMessage = Console.ReadLine();
    if (!string.IsNullOrWhiteSpace(secondMessage))
    {
        var secondResponse = await proxy.RunAsync(secondMessage, session);
        Console.WriteLine($"Agent: {secondResponse.Text}\n");
    }

    if (session is TemporalAgentSession temporalSession)
    {
        await host.Services.GetRequiredService<ITemporalAgentClient>()
            .ShutdownAsync(temporalSession.SessionId);
    }
}
finally
{
    try
    {
        await host.StopAsync();
    }
    catch (OperationCanceledException)
    {
    }
}

sealed class DeterministicEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 1536;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(value =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vector = new float[Dimensions];
            foreach (var token in value.Split(
                         [' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var index = StableHash(token) % Dimensions;
                vector[index] += 1;
            }

            var magnitude = MathF.Sqrt(vector.Sum(component => component * component));
            if (magnitude > 0)
            {
                for (var index = 0; index < vector.Length; index++)
                {
                    vector[index] /= magnitude;
                }
            }

            return new Embedding<float>(vector);
        });

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    private static int StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value.ToLowerInvariant())
        {
            hash = (hash ^ character) * 16777619;
        }

        return (int)(hash & 0x7fffffff);
    }
}
