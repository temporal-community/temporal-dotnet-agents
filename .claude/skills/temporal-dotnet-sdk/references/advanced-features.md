# .NET SDK Advanced Features

## Workflow Updates

Updates allow synchronous, validated mutations to workflow state with return values. Unlike Signals (fire-and-forget), `ExecuteUpdateAsync` blocks the caller until the update handler completes and returns a result — making it the right choice for request/response patterns.

```csharp
[Workflow]
public class OrderWorkflow
{
    private decimal _total;
    private List<string> _items = new();

    [WorkflowRun]
    public async Task<decimal> RunAsync()
    {
        await Workflow.WaitConditionAsync(() => _items.Count > 0);
        return _total;
    }

    [WorkflowUpdate]
    public async Task<int> AddItemAsync(string item, decimal price)
    {
        _items.Add(item);
        _total += price;
        return _items.Count;
    }

    [WorkflowUpdateValidator(nameof(AddItemAsync))]
    public void ValidateAddItem(string item, decimal price)
    {
        if (string.IsNullOrEmpty(item))
            throw new ApplicationFailureException("Item cannot be empty");
        if (price <= 0)
            throw new ApplicationFailureException("Price must be positive");
    }
}

// Client usage — typed return value
var count = await handle.ExecuteUpdateAsync<OrderWorkflow, int>(
    wf => wf.AddItemAsync("Widget", 9.99m));
```

### CRITICAL: Validator Method Name vs Wire Name

`[WorkflowUpdateValidator]` takes the **C# method name** via `nameof()`, NOT the wire name from `[WorkflowUpdate("WireName")]`. Using the wire name causes the worker to fail at startup with `"Cannot find update method named X"`.

```csharp
// CORRECT — nameof() references the C# method name
[WorkflowUpdate("run")]          // wire name is "run"
public async Task<string> RunAgentAsync(RunRequest req) { ... }

[WorkflowUpdateValidator(nameof(RunAgentAsync))]  // C# method name, NOT "run"
public void ValidateRunAgent(RunRequest req) { ... }

// WRONG — using the wire name "run" causes worker startup failure
[WorkflowUpdateValidator("run")]  // ❌ worker fails: "Cannot find update method named 'run'"
public void ValidateRunAgent(RunRequest req) { ... }
```

### WorkflowUpdate as a Human-in-the-Loop Gate

WorkflowUpdate replaces the Signal+Query+polling pattern for human approval flows. The workflow blocks on `WaitConditionAsync` while an activity holds open, and a second update from an external system unblocks it:

```csharp
[Workflow]
public class ApprovalWorkflow
{
    private ApprovalRequest? _pending;
    private ApprovalDecision? _decision;

    // Called from inside an activity (tool) — blocks until human responds
    [WorkflowUpdate("RequestApproval")]
    public async Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request)
    {
        _pending = request;
        var timeout = TimeSpan.FromHours(24);

        var met = await Workflow.WaitConditionAsync(
            () => _decision?.RequestId == request.RequestId,
            timeout: timeout);

        var result = met ? _decision! : new ApprovalDecision
            { RequestId = request.RequestId, Approved = false, Reason = "Timed out." };

        _pending = null;
        _decision = null;
        return result;
    }

    // Called from external system (UI, admin tool) — unblocks the waiting update
    [WorkflowUpdate("SubmitApproval")]
    public Task<ApprovalDecision> SubmitApprovalAsync(ApprovalDecision decision)
    {
        _decision = decision;
        return Task.FromResult(decision);
    }

    [WorkflowQuery("GetPendingApproval")]
    public ApprovalRequest? GetPendingApproval() => _pending;
}
```

The key insight: the activity timeout on `RequestApprovalAsync` must exceed your expected human review time. Set `ActivityStartToCloseTimeout = TimeSpan.FromHours(24)` for a 24-hour review window.

## Continue-as-New for Long-Running Sessions

When workflow history grows large, `Workflow.ContinueAsNewSuggested` signals it's time to start a fresh run. Carry accumulated state forward via the new run's input:

```csharp
[WorkflowRun]
public async Task RunAsync(SessionInput input)
{
    // Restore carried state from prior run
    _history.AddRange(input.CarriedHistory);

    await Workflow.WaitConditionAsync(
        () => _shutdown || (!_processing && Workflow.ContinueAsNewSuggested),
        timeout: input.TimeToLive ?? TimeSpan.FromDays(14));

    if (Workflow.ContinueAsNewSuggested && !_shutdown)
    {
        // IMPORTANT: do not use collection expressions ([.. list]) inside the lambda —
        // use .ToList() or assign to a local variable first.
        var carried = _history.ToList();
        throw Workflow.CreateContinueAsNewException(
            (SessionWorkflow wf) => wf.RunAsync(new SessionInput
            {
                CarriedHistory = carried,
                TimeToLive = input.TimeToLive,
            }));
    }
}
```

**Note**: `Workflow.CreateContinueAsNewException` takes an `Expression<Func<TWorkflow, Task>>`. Collection expression syntax (e.g., `CarriedHistory = [.. list]`) is not valid inside these lambda expressions — use `.ToList()` instead.

## Session Workflow ID Policy

For long-lived session workflows where the client should start the workflow if it's not running, or attach to the existing run if it is, combine `UseExisting` conflict policy with `AllowDuplicate` reuse policy:

```csharp
await client.StartWorkflowAsync(
    (SessionWorkflow wf) => wf.RunAsync(input),
    new WorkflowOptions(workflowId, taskQueue)
    {
        // If already running: attach to existing run (don't start a new one)
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
        // If completed/failed: allow a new run with the same ID
        IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
    });

// Then get a handle WITHOUT a pinned RunId so updates follow the continue-as-new chain
var handle = client.GetWorkflowHandle<SessionWorkflow>(workflowId);
```

This pattern means the client doesn't need to know whether the session is already running — `StartWorkflowAsync` is idempotent.

## Nexus Operations

### WHY: Cross-namespace and cross-cluster service communication
### WHEN:
- **Multi-namespace architectures** - Call operations across Temporal namespaces
- **Service-oriented design** - Expose workflow capabilities as reusable services
- **Cross-cluster communication** - Interact with workflows in different Temporal clusters

### Defining a Nexus Service Interface

```csharp
using NexusRpc;

[NexusService]
public interface IHelloService
{
    static readonly string EndpointName = "nexus-simple-endpoint";

    [NexusOperation]
    EchoOutput Echo(EchoInput input);

    [NexusOperation]
    HelloOutput SayHello(HelloInput input);

    public record EchoInput(string Message);
    public record EchoOutput(string Message);
    public record HelloInput(string Name, string Language);
    public record HelloOutput(string Message);
}
```

### Implementing Nexus Service Handlers

```csharp
using NexusRpc.Handlers;
using Temporalio.Nexus;

[NexusServiceHandler(typeof(IHelloService))]
public class HelloService
{
    // Synchronous operation handler
    [NexusOperationHandler]
    public IOperationHandler<IHelloService.EchoInput, IHelloService.EchoOutput> Echo() =>
        OperationHandler.Sync<IHelloService.EchoInput, IHelloService.EchoOutput>(
            (ctx, input) => new(input.Message));

    // Workflow-backed operation handler
    [NexusOperationHandler]
    public IOperationHandler<IHelloService.HelloInput, IHelloService.HelloOutput> SayHello() =>
        WorkflowRunOperationHandler.FromHandleFactory(
            (WorkflowRunOperationContext context, IHelloService.HelloInput input) =>
                context.StartWorkflowAsync(
                    (HelloHandlerWorkflow wf) => wf.RunAsync(input),
                    new() { Id = context.HandlerContext.RequestId }));
}
```

### Calling Nexus Operations from Workflows

```csharp
[Workflow]
public class CallerWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string name, string language)
    {
        var output = await Workflow.CreateNexusClient<IHelloService>(IHelloService.EndpointName)
            .ExecuteNexusOperationAsync(svc => svc.SayHello(new(name, language)));
        return output.Message;
    }
}
```

## Temporal Cloud Connection (mTLS)

Connect to Temporal Cloud using client certificates for authentication.

```csharp
using Temporalio.Client;

var client = await TemporalClient.ConnectAsync(
    new("your-namespace.tmprl.cloud:7233")
    {
        Namespace = "your-namespace",
        Tls = new()
        {
            // Load client certificate and key
            ClientCert = await File.ReadAllBytesAsync("/path/to/client.pem"),
            ClientPrivateKey = await File.ReadAllBytesAsync("/path/to/client.key"),

            // Optional: Server root CA for self-hosted environments
            // ServerRootCACert = await File.ReadAllBytesAsync("/path/to/ca.pem"),

            // Optional: SNI override for self-signed certificates
            // Domain = "your-server-domain"
        },
    });
```

## Dynamic Workflows, Signals, and Queries

Handle unregistered workflow types or dynamic message routing:

```csharp
[Workflow(Dynamic = true)]
public class DynamicWorkflow
{
    [WorkflowRun]
    public async Task<object?> RunAsync(IRawValue[] args)
    {
        var workflowType = Workflow.Info.WorkflowType;
        // Route based on workflow type
        return workflowType switch
        {
            "TypeA" => await HandleTypeA(args),
            "TypeB" => await HandleTypeB(args),
            _ => throw new ApplicationFailureException($"Unknown type: {workflowType}")
        };
    }

    [WorkflowSignal(Dynamic = true)]
    public async Task HandleDynamicSignalAsync(string signalName, IRawValue[] args)
    {
        Workflow.Logger.LogInformation("Received signal: {Name}", signalName);
    }

    [WorkflowQuery(Dynamic = true)]
    public object? HandleDynamicQuery(string queryName, IRawValue[] args)
    {
        return $"Query {queryName} received";
    }
}
```

## Interceptors

Interceptors provide cross-cutting concerns like logging, metrics, and context propagation.

```csharp
using Temporalio.Client;
using Temporalio.Worker.Interceptors;

public class LoggingInterceptor : IClientInterceptor, IWorkerInterceptor
{
    public ClientOutboundInterceptor InterceptClient(
        ClientOutboundInterceptor nextInterceptor) =>
        new ClientOutbound(nextInterceptor);

    public WorkflowInboundInterceptor InterceptWorkflow(
        WorkflowInboundInterceptor nextInterceptor) =>
        new WorkflowInbound(nextInterceptor);

    private class ClientOutbound : ClientOutboundInterceptor
    {
        public ClientOutbound(ClientOutboundInterceptor next) : base(next) { }

        public override Task<WorkflowHandle<TWorkflow, TResult>>
            StartWorkflowAsync<TWorkflow, TResult>(
                StartWorkflowInput input)
        {
            Console.WriteLine($"Starting workflow: {input.Workflow}");
            return base.StartWorkflowAsync<TWorkflow, TResult>(input);
        }
    }

    private class WorkflowInbound : WorkflowInboundInterceptor
    {
        public WorkflowInbound(WorkflowInboundInterceptor next) : base(next) { }

        public override Task<object?> ExecuteWorkflowAsync(ExecuteWorkflowInput input)
        {
            Workflow.Logger.LogInformation("Executing workflow");
            return base.ExecuteWorkflowAsync(input);
        }
    }
}

// Usage
var client = await TemporalClient.ConnectAsync(new("localhost:7233")
{
    Interceptors = new[] { new LoggingInterceptor() }
});
```

## Context Propagation

Propagate context (like trace IDs, tenant IDs) across workflow boundaries:

```csharp
public class TenantContextInterceptor : IClientInterceptor, IWorkerInterceptor
{
    private const string TenantHeader = "x-tenant-id";

    // Store tenant in async-local for propagation
    private static readonly AsyncLocal<string?> CurrentTenant = new();

    public static string? Tenant
    {
        get => CurrentTenant.Value;
        set => CurrentTenant.Value = value;
    }

    // Implement interceptor methods to read/write headers...
}
```

## Memo and Search Attributes

### Memo (Unindexed Metadata)

```csharp
// Set memo on start
var handle = await client.StartWorkflowAsync(
    (MyWorkflow wf) => wf.RunAsync(),
    new()
    {
        Id = "my-workflow",
        TaskQueue = "my-queue",
        Memo = new Dictionary<string, object>
        {
            ["description"] = "Important workflow",
            ["priority"] = 5
        }
    });

// Update memo in workflow
[WorkflowRun]
public async Task RunAsync()
{
    await Workflow.UpsertMemoAsync(new Dictionary<string, object>
    {
        ["status"] = "processing"
    });
}

// Read memo
var description = (string)Workflow.Memo["description"];
```

### Search Attributes (Indexed, Queryable)

```csharp
// Define search attribute keys
var statusKey = SearchAttributeKey.CreateKeyword("CustomStatus");
var priorityKey = SearchAttributeKey.CreateLong("Priority");

// Set on start
var handle = await client.StartWorkflowAsync(
    (MyWorkflow wf) => wf.RunAsync(),
    new()
    {
        Id = "my-workflow",
        TaskQueue = "my-queue",
        TypedSearchAttributes = new SearchAttributeCollection.Builder()
            .Set(statusKey, "pending")
            .Set(priorityKey, 5)
            .ToSearchAttributeCollection()
    });

// Update in workflow
[WorkflowRun]
public async Task RunAsync()
{
    Workflow.UpsertTypedSearchAttributes(
        statusKey.ValueSet("processing"));

    // Later...
    Workflow.UpsertTypedSearchAttributes(
        statusKey.ValueSet("completed"),
        priorityKey.ValueUnset());
}

// Query workflows by search attributes
var workflows = client.ListWorkflowsAsync("CustomStatus = 'pending'");
```

## Schedules

Create scheduled workflow executions:

```csharp
// Create a schedule
var handle = await client.CreateScheduleAsync(
    "daily-report",
    new ScheduleSpec
    {
        Calendars = new[]
        {
            new ScheduleCalendarSpec
            {
                Hour = new[] { new ScheduleRange(9) },
                DayOfWeek = new[]
                {
                    new ScheduleRange((int)DayOfWeek.Monday, (int)DayOfWeek.Friday)
                }
            }
        }
    },
    new ScheduleAction.StartWorkflow<ReportWorkflow>(
        wf => wf.RunAsync(),
        new()
        {
            Id = $"daily-report-{DateTime.UtcNow:yyyyMMdd}",
            TaskQueue = "reports"
        }));

// Pause/unpause
await handle.PauseAsync("Maintenance window");
await handle.UnpauseAsync();

// Trigger immediately
await handle.TriggerAsync();

// Delete
await handle.DeleteAsync();
```

## External Workflow Handles

Signal or cancel workflows from within another workflow:

```csharp
[WorkflowRun]
public async Task RunAsync(string targetWorkflowId)
{
    // Get handle to external workflow
    var externalHandle = Workflow.GetExternalWorkflowHandle<OtherWorkflow>(targetWorkflowId);

    // Signal it
    await externalHandle.SignalAsync(wf => wf.NotifyAsync("Hello from other workflow"));

    // Or cancel it
    await externalHandle.CancelAsync();
}
```

## Best Practices

1. **Use validators for updates** to reject invalid input before it's stored in history
2. **Prefer typed search attributes** for queryable workflow metadata
3. **Use interceptors** for cross-cutting concerns instead of modifying each workflow
4. **External handles** are for cross-workflow communication within the same Temporal cluster
5. **Use Nexus** for cross-namespace service communication
6. **Use mTLS** for production Temporal Cloud connections
7. **Use local activities** for short, latency-sensitive operations
