# .NET SDK Testing

## Overview

The .NET SDK provides `WorkflowEnvironment` for testing with time-skipping support.

## Time-Skipping Test Environment

```csharp
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

public class WorkflowTests
{
    [Fact]
    public async Task TestWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions($"task-queue-{Guid.NewGuid()}")
                .AddWorkflow<MyWorkflow>()
                .AddAllActivities(new MyActivities()));

        await worker.ExecuteAsync(async () =>
        {
            var result = await env.Client.ExecuteWorkflowAsync(
                (MyWorkflow wf) => wf.RunAsync(),
                new(id: $"wf-{Guid.NewGuid()}", taskQueue: worker.Options.TaskQueue!));

            Assert.Equal("expected", result);
        });
    }
}
```

## Local Test Environment

```csharp
await using var env = await WorkflowEnvironment.StartLocalAsync();
// Real-time execution (no time skipping)
```

## Activity Testing

```csharp
[Fact]
public async Task TestActivity()
{
    var env = new ActivityEnvironment();
    var activities = new MyActivities();

    var result = await env.RunAsync(() => activities.MyActivity("arg"));

    Assert.Equal("expected", result);
}
```

## Testing Signals and Queries

```csharp
[Fact]
public async Task TestSignalsAndQueries()
{
    await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

    using var worker = new TemporalWorker(...);

    await worker.ExecuteAsync(async () =>
    {
        var handle = await env.Client.StartWorkflowAsync(
            (ApprovalWorkflow wf) => wf.RunAsync(),
            new(id: "approval-test", taskQueue: worker.Options.TaskQueue!));

        // Query state
        var status = await handle.QueryAsync(wf => wf.GetStatus());
        Assert.Equal("pending", status);

        // Send signal
        await handle.SignalAsync(wf => wf.ApproveAsync());

        // Wait for result
        var result = await handle.GetResultAsync();
        Assert.Equal("Approved!", result);
    });
}
```

## Workflow Replay Testing

```csharp
[Fact]
public async Task TestReplay()
{
    var replayer = new WorkflowReplayer(
        new WorkflowReplayerOptions().AddWorkflow<MyWorkflow>());

    var history = await FetchWorkflowHistoryAsync("workflow-id");

    await replayer.ReplayWorkflowAsync(
        WorkflowHistory.FromJson("my-workflow-id", historyJson));
}
```

## Mocking Activities

```csharp
public class MockActivities
{
    [Activity]
    public string MyActivity(string input) => "mocked result";
}

[Fact]
public async Task TestWithMockedActivity()
{
    await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

    using var worker = new TemporalWorker(
        env.Client,
        new TemporalWorkerOptions(taskQueue)
            .AddWorkflow<MyWorkflow>()
            .AddAllActivities(new MockActivities()));  // Use mock

    // Run test
}
```

## Custom Search Attributes in Embedded Server

If a workflow calls `Workflow.UpsertTypedSearchAttributes`, the custom attributes **must be pre-registered** on the server before any workflow runs. Without registration, the workflow fails with an opaque `"unexpected workflow task failure"` — there is no useful client-visible error message pointing to the real cause.

Pass `--search-attribute` flags via `DevServerOptions.ExtraArgs` when starting the embedded test server:

```csharp
// Wrap StartLocalAsync to pre-register custom attributes
internal static Task<WorkflowEnvironment> StartLocalAsync()
{
    return WorkflowEnvironment.StartLocalAsync(new()
    {
        DevServerOptions = new()
        {
            ExtraArgs =
            [
                "--search-attribute", "AgentName=Keyword",
                "--search-attribute", "SessionCreatedAt=Datetime",
                "--search-attribute", "TurnCount=Int",
            ]
        }
    });
}
```

Centralize this in a `TestEnvironmentHelper` so any new search attribute added to a workflow only needs to be registered in one place.

## Sharing an Embedded Server Across Tests (IClassFixture)

Starting `WorkflowEnvironment.StartLocalAsync()` per test is slow. Use xUnit's `IClassFixture<T>` with `IAsyncLifetime` to start the server once and share it across all tests in a class. Each test still creates a **unique task queue** to avoid cross-test contamination:

```csharp
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;
    public ITemporalClient Client => Environment.Client;

    public async Task InitializeAsync()
    {
        Environment = await WorkflowEnvironment.StartLocalAsync();
        // Start any shared hosted workers here if needed
    }

    public async Task DisposeAsync() => await Environment.ShutdownAsync();
}

public class MyIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;

    public MyIntegrationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MyTest()
    {
        // Unique task queue per test prevents cross-test contamination
        var taskQueue = $"my-test-{Guid.NewGuid():N}";
        // Build worker, run test against _fixture.Client
    }
}
```

## Testing WorkflowUpdate (Request/Response)

`ExecuteUpdateAsync` is synchronous from the caller's side — it blocks until the update handler completes. Use it in tests for any workflow state that requires a response:

```csharp
// Send an update and get a typed return value
var result = await handle.ExecuteUpdateAsync<MyWorkflow, MyResult>(
    wf => wf.ProcessRequestAsync(new MyRequest("data")));

Assert.Equal("expected", result.Value);
```

The validator runs before the handler. To test that a validator rejects bad input, expect the update call to throw:

```csharp
await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
    () => handle.ExecuteUpdateAsync<MyWorkflow, MyResult>(
        wf => wf.ProcessRequestAsync(new MyRequest(string.Empty))));
```

## Best Practices

1. Use time-skipping for workflows with timers
2. Use unique task queue per test
3. Mock activities for isolated testing
4. Test signal/query handlers explicitly
5. Test replay compatibility when changing workflow code
6. Pre-register custom search attributes via `DevServerOptions.ExtraArgs` — missing registration causes opaque failures
7. Use `IClassFixture<T>` to share one embedded server across all tests in a class
