using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TemporalCommunity.Samples.Mcp;

internal sealed class McpSampleConnection : IAsyncDisposable
{
    private readonly ServiceProvider services;
    private readonly CancellationTokenSource shutdown;
    private readonly Task serverTask;

    private McpSampleConnection(
        ServiceProvider services,
        McpClient client,
        CancellationTokenSource shutdown,
        Task serverTask)
    {
        this.services = services;
        Client = client;
        this.shutdown = shutdown;
        this.serverTask = serverTask;
    }

    internal McpClient Client { get; }

    internal static async Task<McpSampleConnection> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        Pipe clientToServer = new();
        Pipe serverToClient = new();

        var services = new ServiceCollection()
            .AddLogging()
            .AddMcpServer()
            .WithStreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream())
            .WithTools(
            [
                McpServerTool.Create(
                    (string sku) => $"{sku}: 12 units available",
                    new McpServerToolCreateOptions
                    {
                        Name = "lookup_inventory",
                        Description = "Look up current inventory for a product SKU.",
                        ReadOnly = true,
                    }),
                McpServerTool.Create(
                    (string sku) => $"Deleted inventory record for {sku}",
                    new McpServerToolCreateOptions
                    {
                        Name = "delete_inventory",
                        Description = "Delete an inventory record. This is a destructive write.",
                        Destructive = true,
                    }),
                // This unexpected remote tool proves that production registration is an allowlist.
                McpServerTool.Create(
                    () => "internal",
                    new McpServerToolCreateOptions
                    {
                        Name = "server_admin",
                        Description = "An administrative server tool that is not exposed to the model.",
                    }),
            ])
            .Services
            .BuildServiceProvider();

        var shutdown = new CancellationTokenSource();
        var server = services.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(shutdown.Token);

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                NullLoggerFactory.Instance),
            cancellationToken: cancellationToken);

        return new McpSampleConnection(services, client, shutdown, serverTask);
    }

    internal static IReadOnlyList<Tool> LoadPinnedDefinitions(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Tool[]>(json, McpJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException("The pinned MCP tool catalog is empty.");
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await shutdown.CancelAsync();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }

        await services.DisposeAsync();
        shutdown.Dispose();
    }
}

internal sealed class McpSampleChatClient : IChatClient
{
    private int calls;

    public ChatClientMetadata Metadata { get; } = new("mcp-sample");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref calls);
        if (call == 1)
        {
            if (options?.Tools?.Any(t => string.Equals(t.Name, "server_admin", StringComparison.Ordinal)) == true)
            {
                throw new InvalidOperationException("The unapproved server_admin tool reached the model.");
            }

            if (options?.Tools?.Any(t => string.Equals(t.Name, "lookup_inventory", StringComparison.Ordinal)) != true)
            {
                throw new InvalidOperationException("The required lookup_inventory declaration is missing.");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    "inventory-call-1",
                    "lookup_inventory",
                    new Dictionary<string, object?> { ["sku"] = "SKU-123" })])));
        }

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "SKU-123 has 12 units available.")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
