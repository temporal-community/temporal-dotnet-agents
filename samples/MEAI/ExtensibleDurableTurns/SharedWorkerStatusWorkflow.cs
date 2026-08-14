using Temporalio.Workflows;

namespace ExtensibleDurableTurns;

/// <summary>
/// An ordinary typed workflow that shares the worker and Temporal client configuration used by the
/// durable-turn workflow in this sample.
/// </summary>
[Workflow("ExtensibleDurableTurns.SharedWorkerStatusWorkflow")]
public sealed class SharedWorkerStatusWorkflow
{
    [WorkflowRun]
    public Task<SharedWorkerStatus> RunAsync() => Task.FromResult(new SharedWorkerStatus(
        new SharedWorkerSetup("schema-created"),
        new SharedWorkerInventory(6, 20),
        SharedWorkerState.Ready));
}

public sealed record SharedWorkerSetup(string Message);

public sealed record SharedWorkerInventory(int Categories, int Products);

public sealed record SharedWorkerStatus(
    SharedWorkerSetup Setup,
    SharedWorkerInventory Inventory,
    SharedWorkerState Status);

public enum SharedWorkerState
{
    Ready = 0,
}
