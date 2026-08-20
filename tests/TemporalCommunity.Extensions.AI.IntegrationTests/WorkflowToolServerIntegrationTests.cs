using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TemporalCommunity.Extensions.Tests.Shared;
using TemporalCommunity.Samples.Mcp.WorkflowToolServer;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class WorkflowToolServerIntegrationTests
{
    [Fact]
    public async Task AuthenticatedMcpServer_RejectsUnauthorizedCallsBeforeWorkflowStart()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        using var worker = BuildWorker(environment.Client);
        await worker.StartAsync();
        await using var app = await StartServerAsync(environment.Client);
        var endpoint = GetEndpoint(app);

        using var anonymous = new HttpClient();
        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        anonymousRequest.Content = JsonContent.Create(new { });
        using var anonymousResponse = await anonymous.SendAsync(anonymousRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var operationId = $"unauthorized-{Guid.NewGuid():N}";
        await using var reader = await CreateMcpClientAsync(endpoint, "sample:tenant-a:reader");
        var denied = await Assert.ThrowsAsync<McpProtocolException>(async () => await reader.CallToolAsync(
            "start_or_join_operation",
            new Dictionary<string, object?>
            {
                ["operationId"] = operationId,
                ["workItem"] = "inventory-refresh",
            }));
        Assert.Contains("authorization", denied.Message, StringComparison.OrdinalIgnoreCase);

        var derivedId = WorkflowOperationService.DeriveWorkflowId("tenant-a", operationId);
        await Assert.ThrowsAsync<RpcException>(
            () => environment.Client.GetWorkflowHandle(derivedId).DescribeAsync());

        await using var writer = await CreateMcpClientAsync(endpoint, "sample:tenant-a:writer");
        var tools = await writer.ListToolsAsync();
        Assert.Equal(
            ["start_or_join_operation", "start_unique_operation"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(tools, AssertWorkflowResultSchema);

        var allowed = await writer.CallToolAsync(
            "start_or_join_operation",
            new Dictionary<string, object?>
            {
                ["operationId"] = operationId,
                ["workItem"] = "inventory-refresh",
            });
        var allowedResult = DeserializeResult(allowed);
        Assert.NotEqual(true, allowed.IsError);
        Assert.Equal(operationId, allowedResult.OperationId);
        Assert.Equal("completed", allowedResult.Status);
        Assert.NotNull(allowedResult.Result);
        Assert.Null(allowedResult.ErrorCode);
        Assert.Equal(allowedResult, JsonSerializer.Deserialize<WorkflowToolResult>(ReadText(allowed), McpJsonUtilities.DefaultOptions));
        AssertTenantSafe(allowed, derivedId);

        await worker.StopAsync();
    }

    [Fact]
    public async Task StartUnique_DuplicateReturnsStructuredConflict()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        using var worker = BuildWorker(environment.Client);
        await worker.StartAsync();
        await using var app = await StartServerAsync(environment.Client);
        await using var writer = await CreateMcpClientAsync(GetEndpoint(app), "sample:tenant-a:writer");
        var operationId = $"structured-conflict-{Guid.NewGuid():N}";
        var arguments = new Dictionary<string, object?>
        {
            ["operationId"] = operationId,
            ["workItem"] = "inventory-refresh",
        };

        var completed = await writer.CallToolAsync("start_unique_operation", arguments);
        var conflict = await writer.CallToolAsync("start_unique_operation", arguments);

        Assert.NotEqual(true, completed.IsError);
        Assert.Equal("completed", DeserializeResult(completed).Status);
        var conflictResult = DeserializeResult(conflict);
        Assert.True(conflict.IsError);
        Assert.Equal(operationId, conflictResult.OperationId);
        Assert.Equal("conflict", conflictResult.Status);
        Assert.Null(conflictResult.Result);
        Assert.Equal("operation_already_exists", conflictResult.ErrorCode);
        Assert.Equal(conflictResult, JsonSerializer.Deserialize<WorkflowToolResult>(ReadText(conflict), McpJsonUtilities.DefaultOptions));
        AssertTenantSafe(
            conflict,
            WorkflowOperationService.DeriveWorkflowId("tenant-a", operationId));

        await worker.StopAsync();
    }

    [Fact]
    public async Task StartOrJoin_RunningAndCompletedRetriesReturnSameBusinessResult()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        using var worker = BuildWorker(environment.Client);
        await worker.StartAsync();
        var service = new WorkflowOperationService(
            environment.Client,
            new InMemoryWorkflowOperationLedger());
        var operationId = $"join-{Guid.NewGuid():N}";

        var first = service.StartOrJoinAsync(
            "tenant-a",
            operationId,
            "reconcile",
            CancellationToken.None);
        var joined = service.StartOrJoinAsync(
            "tenant-a",
            operationId,
            "reconcile",
            CancellationToken.None);
        var concurrentResults = await Task.WhenAll(first, joined);

        Assert.All(concurrentResults, result => Assert.Equal("completed", result.Status));
        Assert.Equal(concurrentResults[0], concurrentResults[1]);
        var retained = await service.StartOrJoinAsync(
            "tenant-a",
            operationId,
            "different-value-is-not-reexecuted",
            CancellationToken.None);
        Assert.Equal(concurrentResults[0], retained);

        await worker.StopAsync();
    }

    [Fact]
    public async Task TenantIdentityAndUniqueMode_AreFailClosed()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        using var worker = BuildWorker(environment.Client);
        await worker.StartAsync();
        var service = new WorkflowOperationService(
            environment.Client,
            new InMemoryWorkflowOperationLedger());
        var operationId = $"tenant-{Guid.NewGuid():N}";

        Assert.NotEqual(
            WorkflowOperationService.DeriveWorkflowId("tenant-a", operationId),
            WorkflowOperationService.DeriveWorkflowId("tenant-b", operationId));

        var first = await service.StartUniqueAsync(
            "tenant-a",
            operationId,
            "reconcile",
            CancellationToken.None);
        var duplicate = await service.StartUniqueAsync(
            "tenant-a",
            operationId,
            "reconcile",
            CancellationToken.None);
        var otherTenant = await service.StartUniqueAsync(
            "tenant-b",
            operationId,
            "reconcile",
            CancellationToken.None);

        Assert.Equal("completed", first.Status);
        Assert.Equal("operation_already_exists", duplicate.ErrorCode);
        Assert.Equal("completed", otherTenant.Status);
        Assert.Equal("reconcile completed", first.Result);
        Assert.Equal(first.Result, otherTenant.Result);

        await worker.StopAsync();
    }

    [Theory]
    [InlineData("fail", "operation_failed")]
    [InlineData("cancel", "operation_canceled")]
    [InlineData("terminate", "operation_terminated")]
    [InlineData("timeout", "operation_timed_out")]
    public async Task StartOrJoin_ClosedFailureReturnsStructuredTenantSafeError(
        string closure,
        string expectedErrorCode)
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        using var worker = BuildWorker(environment.Client);
        await worker.StartAsync();
        var operationId = $"closed-{closure}-{Guid.NewGuid():N}";
        var workflowId = WorkflowOperationService.DeriveWorkflowId("tenant-a", operationId);
        var options = new WorkflowOptions(workflowId, WorkflowToolServerConstants.TaskQueue)
        {
            IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
            RunTimeout = closure == "timeout" ? TimeSpan.FromMilliseconds(300) : null,
        };
        var handle = await environment.Client.StartWorkflowAsync(
            (WorkflowOperationWorkflow workflow) => workflow.RunAsync(new(
                "tenant-a",
                operationId,
                closure == "fail" ? "fail" : "wait")),
            options);

        if (closure == "cancel")
        {
            await handle.CancelAsync();
        }
        else if (closure == "terminate")
        {
            await handle.TerminateAsync("integration-test");
        }

        await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
        await using var app = await StartServerAsync(environment.Client);
        await using var writer = await CreateMcpClientAsync(GetEndpoint(app), "sample:tenant-a:writer");
        var callResult = await writer.CallToolAsync(
            "start_or_join_operation",
            new Dictionary<string, object?>
            {
                ["operationId"] = operationId,
                ["workItem"] = "ignored-on-retry",
            });
        var result = DeserializeResult(callResult);

        Assert.True(callResult.IsError);
        Assert.Equal(operationId, result.OperationId);
        Assert.Equal("failed", result.Status);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Null(result.Result);
        Assert.Equal(result, JsonSerializer.Deserialize<WorkflowToolResult>(ReadText(callResult), McpJsonUtilities.DefaultOptions));
        AssertTenantSafe(callResult, workflowId);

        await worker.StopAsync();
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelDurableWork()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        using var worker = BuildWorker(environment.Client);
        await worker.StartAsync();
        var operationId = $"caller-cancel-{Guid.NewGuid():N}";
        var service = new WorkflowOperationService(
            environment.Client,
            new InMemoryWorkflowOperationLedger());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartOrJoinAsync(
            "tenant-a",
            operationId,
            "wait",
            cancellation.Token));

        var workflowId = WorkflowOperationService.DeriveWorkflowId("tenant-a", operationId);
        var handle = environment.Client.GetWorkflowHandle(workflowId);
        Assert.Equal(WorkflowExecutionStatus.Running, (await handle.DescribeAsync()).Status);
        await handle.CancelAsync();
        await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());

        await worker.StopAsync();
    }

    [Fact]
    public async Task ApplicationLedgerRemainsAuthorityAfterTemporalRetentionBoundary()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        var ledger = new InMemoryWorkflowOperationLedger();
        var operationId = $"retained-{Guid.NewGuid():N}";
        var retained = new WorkflowToolResult(operationId, "completed", "retained-result");
        ledger.Store(new("tenant-a", operationId), retained);
        var service = new WorkflowOperationService(environment.Client, ledger);

        var result = await service.StartOrJoinAsync(
            "tenant-a",
            operationId,
            "must-not-start",
            CancellationToken.None);

        Assert.Equal(retained, result);
        await Assert.ThrowsAsync<RpcException>(() => environment.Client
            .GetWorkflowHandle(WorkflowOperationService.DeriveWorkflowId("tenant-a", operationId))
            .DescribeAsync());
    }

    private static IHost BuildWorker(ITemporalClient client)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services
            .AddHostedTemporalWorker(WorkflowToolServerConstants.TaskQueue)
            .AddWorkflow<WorkflowOperationWorkflow>();
        return builder.Build();
    }

    private static async Task<WebApplication> StartServerAsync(ITemporalClient client)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddWorkflowToolServer(client);
        var app = builder.Build();
        app.MapWorkflowToolServer();
        await app.StartAsync();
        return app;
    }

    private static Uri GetEndpoint(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(new Uri(addresses.Single()), "/mcp");
    }

    private static Task<McpClient> CreateMcpClientAsync(Uri endpoint, string token) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {token}",
            },
        }));

    private static WorkflowToolResult DeserializeResult(CallToolResult result) =>
        result.StructuredContent?.Deserialize<WorkflowToolResult>(McpJsonUtilities.DefaultOptions)
        ?? throw new Xunit.Sdk.XunitException("The MCP result did not contain structured content.");

    private static string ReadText(CallToolResult result) =>
        Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    private static void AssertWorkflowResultSchema(McpClientTool tool)
    {
        var schema = tool.ProtocolTool.OutputSchema
            ?? throw new Xunit.Sdk.XunitException($"Tool '{tool.Name}' did not advertise an output schema.");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("operationId").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("status").GetProperty("type").GetString());
        Assert.True(properties.TryGetProperty("result", out _));
        Assert.True(properties.TryGetProperty("errorCode", out _));
    }

    private static void AssertTenantSafe(CallToolResult result, string derivedWorkflowId)
    {
        var serialized = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        Assert.DoesNotContain("tenant-a", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(derivedWorkflowId, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkflowFailedException", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", serialized, StringComparison.OrdinalIgnoreCase);
    }
}
