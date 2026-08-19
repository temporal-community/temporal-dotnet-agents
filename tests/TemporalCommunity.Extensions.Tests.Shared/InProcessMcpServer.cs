using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TemporalCommunity.Extensions.Tests.Shared;

public sealed class InProcessMcpServer : IAsyncDisposable
{
    private const string PinnedJson = """
        [
          {
            "name": "lookup_inventory",
            "description": "Look up inventory by SKU.",
            "inputSchema": {
              "type": "object",
              "properties": { "sku": { "type": "string" } },
              "required": ["sku"]
            }
          },
          {
            "name": "delete_inventory",
            "description": "Delete an inventory record.",
            "inputSchema": {
              "type": "object",
              "properties": { "sku": { "type": "string" } },
              "required": ["sku"]
            }
          }
        ]
        """;

    private readonly IAsyncDisposable services;
    private readonly CancellationTokenSource shutdown;
    private readonly Task serverTask;

    private InProcessMcpServer(
        IAsyncDisposable services,
        McpClient client,
        CancellationTokenSource shutdown,
        Task serverTask)
    {
        this.services = services;
        Client = client;
        this.shutdown = shutdown;
        this.serverTask = serverTask;
    }

    public McpClient Client { get; }

    public static IReadOnlyList<Tool> PinnedDefinitions { get; } =
        JsonSerializer.Deserialize<Tool[]>(PinnedJson, McpJsonUtilities.DefaultOptions)!;

    public static async Task<InProcessMcpServer> CreateAsync(
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
                    (string sku) => $"{sku}:12",
                    new McpServerToolCreateOptions
                    {
                        Name = "lookup_inventory",
                        Description = "Look up inventory by SKU.",
                        ReadOnly = true,
                    }),
                McpServerTool.Create(
                    (string sku) => $"deleted:{sku}",
                    new McpServerToolCreateOptions
                    {
                        Name = "delete_inventory",
                        Description = "Delete an inventory record.",
                        Destructive = true,
                    }),
                McpServerTool.Create(
                    () => "admin",
                    new McpServerToolCreateOptions
                    {
                        Name = "server_admin",
                        Description = "Not part of the approved model surface.",
                    }),
            ])
            .Services
            .BuildServiceProvider();

        var shutdown = new CancellationTokenSource();
        var serverTask = services.GetRequiredService<McpServer>().RunAsync(shutdown.Token);
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                NullLoggerFactory.Instance),
            cancellationToken: cancellationToken);

        return new InProcessMcpServer(services, client, shutdown, serverTask);
    }

    public static IReadOnlyDictionary<string, McpClientTool> CreatePinnedTools(McpClient client) =>
        PinnedDefinitions
            .Select(definition => new McpClientTool(client, definition))
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);

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
