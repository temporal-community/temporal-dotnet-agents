using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;

const string invokeFunctionActivity = "TemporalCommunity.Extensions.AI.InvokeFunction";
const string getChatStepActivity = "TemporalCommunity.Extensions.AI.GetChatStep";
const string resolveToolsetsActivity = "TemporalCommunity.Extensions.AI.ResolveDurableToolsets";

var expectedAsset = args.SingleOrDefault() switch
{
    "net10" => ".NETCoreApp,Version=v10.0",
    "netstandard" => ".NETStandard,Version=v2.1",
    _ => throw new InvalidOperationException("Pass net10 or netstandard."),
};
AssertPackageAssembly("TemporalCommunity.Extensions.AI", expectedAsset);
AssertPackageAssembly("TemporalCommunity.Extensions.Agents", expectedAsset);

// Exercise the public registration family from the packed asset, including IEnumerable overloads.
var registrationProbeServices = new ServiceCollection();
var registrationProbeWorker = registrationProbeServices
    .AddHostedTemporalWorker("packed-registration-probe")
    .AddDurableAI(options => options.RegisterDefaultWorkflow = false);
registrationProbeWorker.AddDurableTool(
    AIFunctionFactory.Create(() => "single", "single_tool"));
registrationProbeWorker.AddDurableTools((IEnumerable<AIFunction>)new[]
{
    AIFunctionFactory.Create(() => "first", "first_tool"),
    AIFunctionFactory.Create(() => "second", "second_tool"),
});
registrationProbeWorker.AddDurableToolset("collection", tools => tools.AddTools(new[]
{
    AIFunctionFactory.Create(() => "third", "third_tool"),
}));
using var registrationProbeProvider = registrationProbeServices.BuildServiceProvider();

await using var environment = await WorkflowEnvironment.StartLocalAsync(new()
{
    DevServerOptions = new() { DownloadVersion = "v1.8.0" },
});
environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
var targetHost = environment.Client.Connection.Options.TargetHost
    ?? throw new InvalidOperationException("Embedded Temporal target host is unavailable.");
var taskQueue = $"packed-extensible-turn-{Guid.NewGuid():N}";
var clientServices = new ServiceCollection();
clientServices.AddLogging();
clientServices.AddTemporalClient(targetHost, environment.Client.Options.Namespace);
clientServices
    .AddDurableChatWorkflowInputFactory(taskQueue, options =>
    {
        options.ActivityTimeout = TimeSpan.FromSeconds(30);
        options.MaxToolCallsPerTurn = 4;
    });
await using var clientProvider = clientServices.BuildServiceProvider();
var clientOptions = clientProvider
    .GetRequiredService<IOptions<TemporalClientConnectOptions>>()
    .Value;
Assert(
    ReferenceEquals(clientOptions.DataConverter, DurableAIDataConverter.Instance),
    "Client-only registration did not select DurableAIDataConverter.Instance.");
var client = clientProvider.GetRequiredService<ITemporalClient>();
var workflowInput = clientProvider
    .GetRequiredService<IDurableChatWorkflowInputFactory>()
    .Create();

var scripted = new PackageScriptedChatClient();
var scopes = new ScopeTracker();
var workerBuilder = Host.CreateApplicationBuilder();
var workerServices = workerBuilder.Services;
workerServices.AddLogging();
workerServices.AddSingleton<IChatClient>(scripted);
workerServices.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new NoopEmbeddingGenerator());
workerServices.AddHttpClient("packed-attempt");
workerServices.AddSingleton(scopes);
workerServices.AddScoped<ScopedToolService>();
var worker = workerServices
    .AddHostedTemporalWorker(targetHost, environment.Client.Options.Namespace, taskQueue)
    .AddDurableAI(options => options.RegisterDefaultWorkflow = false)
    .AddWorkflow<PackedTurnWorkflow>();
worker.AddDurableToolset("packed", tools => tools.AddDurableToolFactory<PackedRequestData, PackedTurnState>(
    AIFunctionFactory.Create(
        (string value) => string.Empty,
        "packed_tool",
        "Processes one value.").AsDeclarationOnly(),
    (services, context) =>
    {
        var scoped = services.GetRequiredService<ScopedToolService>();
        return new DurableToolActivation<PackedTurnState>
        {
            Function = AIFunctionFactory.Create(
                (string value) =>
                {
                    scopes.Observed.Add(new ToolObservation(
                        context.RequestData.OperationId,
                        context.TurnState?.Revision ?? 0,
                        scoped.Id,
                        context.Metadata.Attempt));
                    if (context.Metadata.Attempt == 1)
                    {
                        throw new InvalidOperationException("Injected first-attempt failure.");
                    }

                    return $"processed:{value}";
                },
                "packed_tool",
                "Processes one value."),
            CompleteState = (_, _) => ValueTask.FromResult(
                DurableStateUpdate<PackedTurnState>.Replace(
                    new PackedTurnState(
                        (context.TurnState?.Revision ?? 0) + 1,
                        [context.RequestData.OperationId]))),
        };
    },
    options => options.WithMaxAttempts(2).RequireApproval()));
using var workerHost = workerBuilder.Build();
Assert(
    workerHost.Services.GetService<ITemporalClient>() is null,
    "Activity-only worker unexpectedly registered ITemporalClient.");
await workerHost.StartAsync();

try
{
    var workflowId = $"packed-extensible-turn-{Guid.NewGuid():N}";
    var handle = await client.StartWorkflowAsync(
        (PackedTurnWorkflow workflow) => workflow.RunAsync(workflowInput),
        new WorkflowOptions(workflowId, taskQueue));
    var request = CreateRequest("business-operation-1");
    var turnTask = handle.ExecuteUpdateAsync(
        workflow => workflow.TurnAsync(request),
        new WorkflowUpdateOptions { Id = "packed-turn-1" });
    var pending = await WaitForApprovalAsync(handle, turnTask);
    var approval = await handle.ExecuteUpdateAsync(
        workflow => workflow.ResolveApprovalAsync(new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved = true,
            Reason = "packed-consumer-approved",
        }));
    Assert(approval.Status == DurableApprovalResolutionStatus.Accepted,
        "Packed consumer approval was not accepted.");
    var result = await turnTask;

    Assert(result.CompletionReason == DurableTurnCompletionReason.FinalResponse,
        "Typed turn did not reach a final response.");
    var finalState = result.FinalTurnState
        ?? throw new InvalidOperationException("Typed turn returned no final state.");
    Assert(finalState.Revision == 1, "Sequential state was not completed.");
    Assert(finalState.Receipts.SequenceEqual(["business-operation-1"]),
        "Typed state did not carry the application request identity.");
    var observations = scopes.Observed.OrderBy(item => item.Attempt).ToArray();
    Assert(observations.Length == 2, "Expected one failed and one successful tool activity attempt.");
    Assert(observations.All(item => item.OperationId == "business-operation-1"),
        "RequestData did not reach each factory attempt.");
    Assert(observations.All(item => item.Revision == 0),
        "InitialTurnState was not recovered for the retry.");
    Assert(observations.Select(item => item.Attempt).SequenceEqual([1, 2]),
        "The tool activity did not retry exactly once.");
    Assert(observations.Select(item => item.ScopeId).Distinct().Count() == 2,
        "Activity attempts reused a scoped service.");
    await WaitUntilAsync(() => observations.All(item => scopes.Disposed.Contains(item.ScopeId)));

    var schema = scripted.ToolSchema
        ?? throw new InvalidOperationException("Model did not receive the frozen tool declaration.");
    Assert(schema.Contains("value", StringComparison.Ordinal), "Model schema omitted tool argument.");
    Assert(!schema.Contains("business-operation-1", StringComparison.Ordinal),
        "RequestData leaked into the model schema.");
    Assert(!schema.Contains("revision", StringComparison.OrdinalIgnoreCase),
        "Turn state leaked into the model schema.");

    var activityTypes = new List<string>();
    await foreach (var historyEvent in handle.FetchHistoryEventsAsync())
    {
        if (historyEvent.ActivityTaskScheduledEventAttributes is { } scheduled)
        {
            activityTypes.Add(scheduled.ActivityType.Name);
        }
    }
    Assert(activityTypes.Count(type => type == getChatStepActivity) == 2,
        "Expected two separate model activities.");
    Assert(activityTypes.Count(type => type == invokeFunctionActivity) == 1,
        "Expected one separate tool activity.");
    Assert(activityTypes.Count(type => type == resolveToolsetsActivity) == 1,
        "Expected exactly one worker-owned toolset resolver activity.");

    var deniedHandle = await client.StartWorkflowAsync(
        (PackedTurnWorkflow workflow) => workflow.RunAsync(workflowInput),
        new WorkflowOptions($"packed-denied-{Guid.NewGuid():N}", taskQueue));
    var deniedRequest = CreateRequest("denied-operation");
    var deniedTask = deniedHandle.ExecuteUpdateAsync(
        workflow => workflow.TurnAsync(deniedRequest),
        new WorkflowUpdateOptions { Id = "packed-denied-turn" });
    var deniedPending = await WaitForApprovalAsync(deniedHandle, deniedTask);
    await deniedHandle.ExecuteUpdateAsync(
        workflow => workflow.ResolveApprovalAsync(new DurableApprovalDecision
        {
            RequestId = deniedPending.RequestId,
            Approved = false,
            Reason = "packed-consumer-denied",
        }));
    var deniedResult = await deniedTask;
    Assert(deniedResult.CompletionReason == DurableTurnCompletionReason.FinalResponse,
        "Denied turn did not return a final model response.");
    Assert(scopes.Observed.Count == 2, "Denied tool call reached the implementation.");
    await deniedHandle.SignalAsync(workflow => workflow.RequestShutdownAsync());

    var callsBeforeInvalid = scripted.CallCount;
    var invalidRequest = new DurableTurnRequest<PackedRequestData, PackedTurnState>
    {
        Messages = [new ChatMessage(ChatRole.User, "invalid-dispatch")],
        RequestData = new PackedRequestData("invalid-dispatch"),
        InitialTurnState = new PackedTurnState(0, []),
        CorrelationId = "invalid-dispatch",
        Options = new DurableTurnOptions { DispatchMode = (DurableToolDispatchMode)99 },
    };
    await AssertThrowsAsync(() => handle.ExecuteUpdateAsync(
        workflow => workflow.TurnAsync(invalidRequest),
        new WorkflowUpdateOptions { Id = "packed-invalid-dispatch" }));
    Assert(scripted.CallCount == callsBeforeInvalid, "Unknown dispatch reached the model.");
    Assert(scopes.Observed.Count == 2, "Unknown dispatch reached the tool factory.");

    var duplicateRequest = CreateRequest("duplicate-turn");
    await AssertThrowsAsync(() => handle.ExecuteUpdateAsync(
        workflow => workflow.DoubleTurnAsync(duplicateRequest),
        new WorkflowUpdateOptions { Id = "packed-double-turn" }));
    Assert(scripted.CallCount == callsBeforeInvalid + 1,
        "A second managed turn in one Update reached the model.");

    await handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
}
finally
{
    await workerHost.StopAsync();
}

Console.WriteLine($"Packed extensible-turn smoke passed for {args[0]}.");

static DurableTurnRequest<PackedRequestData, PackedTurnState> CreateRequest(string operationId) => new()
{
    Messages = [new ChatMessage(ChatRole.User, operationId)],
    RequestData = new PackedRequestData(operationId),
    InitialTurnState = new PackedTurnState(0, []),
    CorrelationId = operationId,
};

static void AssertPackageAssembly(string assemblyName, string expectedFramework)
{
    var assembly = Assembly.Load(assemblyName);
    var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
    Assert(framework == expectedFramework,
        $"{assemblyName} runtime asset was {framework ?? "(missing)"}; expected {expectedFramework}.");
}

static async Task AssertThrowsAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch
    {
        return;
    }

    throw new InvalidOperationException("Expected operation to fail.");
}

static async Task WaitUntilAsync(Func<bool> condition)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (!condition() && DateTime.UtcNow < deadline)
    {
        await Task.Delay(25);
    }

    Assert(condition(), "Timed out waiting for the activity scope to be disposed.");
}

static async Task<DurableApprovalRequest> WaitForApprovalAsync(
    WorkflowHandle<PackedTurnWorkflow> handle,
    Task turnTask)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
    while (!turnTask.IsCompleted && DateTime.UtcNow < deadline)
    {
        var pending = await handle.QueryAsync(workflow => workflow.GetPendingApproval());
        if (pending is not null)
        {
            return pending;
        }

        await Task.Delay(25);
    }

    throw new InvalidOperationException("Timed out waiting for packed-consumer approval.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public sealed record PackedRequestData(string OperationId);
public sealed record PackedTurnState(int Revision, IReadOnlyList<string> Receipts);

[Workflow("Packed.ExtensibleDurableTurnWorkflow")]
public sealed class PackedTurnWorkflow
    : DurableToolWorkflowBase<PackedRequestData, PackedTurnState>
{
    protected override IReadOnlyList<string>? DurableToolsetBaselineIds => ["packed"];

    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<PackedTurnState>> TurnAsync(
        DurableTurnRequest<PackedRequestData, PackedTurnState> request) =>
        RunDurableTurnAsync(request);

    [WorkflowUpdate("DoubleTurn")]
    public async Task DoubleTurnAsync(
        DurableTurnRequest<PackedRequestData, PackedTurnState> request)
    {
        await RunDurableTurnAsync(request);
        await RunDurableTurnAsync(request);
    }
}

internal sealed class ScopeTracker
{
    public ConcurrentBag<Guid> Created { get; } = [];
    public ConcurrentBag<Guid> Disposed { get; } = [];
    public ConcurrentBag<ToolObservation> Observed { get; } = [];
}

internal sealed record ToolObservation(string OperationId, int Revision, Guid ScopeId, int Attempt);

internal sealed class ScopedToolService : IDisposable
{
    private readonly ScopeTracker _tracker;

    public ScopedToolService(IHttpClientFactory clients, ScopeTracker tracker)
    {
        _tracker = tracker;
        using var client = clients.CreateClient("packed-attempt");
        Id = Guid.NewGuid();
        tracker.Created.Add(Id);
    }

    public Guid Id { get; }

    public void Dispose() => _tracker.Disposed.Add(Id);
}

internal sealed class PackageScriptedChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);
    public string? ToolSchema { get; private set; }
    public ChatClientMetadata Metadata { get; } = new("packed-scripted");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Next(messages, options));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var update in Next(messages, options).ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    private ChatResponse Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var call = Interlocked.Increment(ref _callCount);
        var materialized = messages.ToList();
        var isDuplicateGuardProbe = materialized.Any(message =>
            message.Text?.Contains("duplicate-turn", StringComparison.Ordinal) == true);
        var hasToolResult = materialized.SelectMany(message => message.Contents)
            .Any(content => content is FunctionResultContent);
        if (!isDuplicateGuardProbe && !hasToolResult)
        {
            var tool = options?.Tools?.Single()
                ?? throw new InvalidOperationException("Frozen declaration was not sent to model.");
            ToolSchema = tool is AIFunctionDeclaration function
                ? function.JsonSchema.GetRawText()
                : throw new InvalidOperationException("Expected an AIFunctionDeclaration.");
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "packed-call-1",
                    "packed_tool",
                    new Dictionary<string, object?> { ["value"] = "payload" }),
            ]));
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"final-{call}"));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class NoopEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } = new("noop", null, null, 1);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(_ => new Embedding<float>(new[] { 0f })).ToList()));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
