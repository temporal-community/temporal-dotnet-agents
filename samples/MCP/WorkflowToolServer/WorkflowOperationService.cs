using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace TemporalCommunity.Samples.Mcp.WorkflowToolServer;

public sealed class WorkflowOperationService(
    ITemporalClient client,
    IWorkflowOperationLedger ledger)
{
    public async Task<WorkflowToolResult> StartUniqueAsync(
        string tenantId,
        string operationId,
        string workItem,
        CancellationToken cancellationToken)
    {
        Validate(tenantId, operationId, workItem);
        var key = new WorkflowOperationKey(tenantId, operationId);
        if (ledger.TryGet(key, out _))
        {
            return new(operationId, "conflict", ErrorCode: "operation_already_exists");
        }

        try
        {
            var handle = await client.StartWorkflowAsync(
                (WorkflowOperationWorkflow workflow) => workflow.RunAsync(
                    new WorkflowOperationInput(tenantId, operationId, workItem)),
                CreateOptions(tenantId, operationId, WorkflowIdConflictPolicy.Fail, cancellationToken));
            return await AwaitAndRecordAsync(handle, key, operationId, cancellationToken);
        }
        catch (WorkflowAlreadyStartedException)
        {
            return new(operationId, "conflict", ErrorCode: "operation_already_exists");
        }
    }

    public async Task<WorkflowToolResult> StartOrJoinAsync(
        string tenantId,
        string operationId,
        string workItem,
        CancellationToken cancellationToken)
    {
        Validate(tenantId, operationId, workItem);
        var key = new WorkflowOperationKey(tenantId, operationId);
        if (ledger.TryGet(key, out var retained))
        {
            return retained;
        }

        var workflowId = DeriveWorkflowId(tenantId, operationId);
        WorkflowHandle handle;
        try
        {
            handle = await client.StartWorkflowAsync(
                (WorkflowOperationWorkflow workflow) => workflow.RunAsync(
                    new WorkflowOperationInput(tenantId, operationId, workItem)),
                CreateOptions(
                    tenantId,
                    operationId,
                    WorkflowIdConflictPolicy.UseExisting,
                    cancellationToken));
        }
        catch (WorkflowAlreadyStartedException)
        {
            // The conflicting execution is closed and reuse is rejected. Recover its terminal
            // result rather than returning a misleading joined success.
            handle = client.GetWorkflowHandle<WorkflowOperationWorkflow>(workflowId);
        }

        return await AwaitAndRecordAsync(handle, key, operationId, cancellationToken);
    }

    public static string DeriveWorkflowId(string tenantId, string operationId)
    {
        ValidatePart(tenantId, nameof(tenantId));
        ValidatePart(operationId, nameof(operationId));
        using var input = new MemoryStream();
        WritePart(input, "workflow-tool-server-v1");
        WritePart(input, tenantId);
        WritePart(input, operationId);
        var digest = SHA256.HashData(input.ToArray());
        return $"mcp-operation-v1-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static WorkflowOptions CreateOptions(
        string tenantId,
        string operationId,
        WorkflowIdConflictPolicy conflictPolicy,
        CancellationToken cancellationToken) => new(
            DeriveWorkflowId(tenantId, operationId),
            WorkflowToolServerConstants.TaskQueue)
        {
            IdConflictPolicy = conflictPolicy,
            IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
            Rpc = new RpcOptions { CancellationToken = cancellationToken },
        };

    private async Task<WorkflowToolResult> AwaitAndRecordAsync(
        WorkflowHandle handle,
        WorkflowOperationKey key,
        string operationId,
        CancellationToken cancellationToken)
    {
        WorkflowToolResult result;
        try
        {
            var completed = await handle.GetResultAsync<WorkflowOperationResult>(
                true,
                new RpcOptions { CancellationToken = cancellationToken });
            result = new(operationId, "completed", completed.Result);
        }
        catch (WorkflowFailedException exception)
        {
            // Do not expose workflow/run IDs, stack traces, tenant data, or another tenant's
            // existence in the public result.
            var errorCode = exception.InnerException switch
            {
                CanceledFailureException => "operation_canceled",
                TimeoutFailureException => "operation_timed_out",
                TerminatedFailureException => "operation_terminated",
                _ => "operation_failed",
            };
            result = new(operationId, "failed", ErrorCode: errorCode);
        }

        ledger.Store(key, result);
        return result;
    }

    private static void Validate(string tenantId, string operationId, string workItem)
    {
        ValidatePart(tenantId, nameof(tenantId));
        ValidatePart(operationId, nameof(operationId));
        ValidatePart(workItem, nameof(workItem));
    }

    private static void ValidatePart(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (Encoding.UTF8.GetByteCount(value) > 256)
        {
            throw new ArgumentException($"{name} must not exceed 256 UTF-8 bytes.", name);
        }
    }

    private static void WritePart(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes, 0, bytes.Length);
    }
}
