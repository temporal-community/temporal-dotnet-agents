using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TemporalCommunity.Samples.Mcp.WorkflowToolServer;

[McpServerToolType]
public sealed class WorkflowOperationTools(WorkflowOperationService operations)
{
    [McpServerTool(
        Name = "start_unique_operation",
        Destructive = true,
        ReadOnly = false,
        Idempotent = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(WorkflowToolResult))]
    [Authorize(Policy = WorkflowToolServerConstants.StartPolicy)]
    [Description("Starts a new durable operation and rejects a duplicate operation ID.")]
    public async Task<CallToolResult> StartUniqueAsync(
        RequestContext<CallToolRequestParams> request,
        [Description("Application-owned operation ID, not a Temporal workflow ID.")] string operationId,
        [Description("The work item to process.")] string workItem,
        CancellationToken cancellationToken) => ToCallToolResult(await operations.StartUniqueAsync(
            GetAuthorizedTenant(request.User),
            operationId,
            workItem,
            cancellationToken).ConfigureAwait(false));

    [McpServerTool(
        Name = "start_or_join_operation",
        Destructive = true,
        ReadOnly = false,
        Idempotent = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(WorkflowToolResult))]
    [Authorize(Policy = WorkflowToolServerConstants.StartPolicy)]
    [Description("Starts a durable operation or joins the same tenant-scoped operation.")]
    public async Task<CallToolResult> StartOrJoinAsync(
        RequestContext<CallToolRequestParams> request,
        [Description("Application-owned operation ID, not a Temporal workflow ID.")] string operationId,
        [Description("The work item to process.")] string workItem,
        CancellationToken cancellationToken) => ToCallToolResult(await operations.StartOrJoinAsync(
            GetAuthorizedTenant(request.User),
            operationId,
            workItem,
            cancellationToken).ConfigureAwait(false));

    internal static CallToolResult ToCallToolResult(WorkflowToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var structured = JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            IsError = result.Status is "conflict" or "failed",
        };
    }

    private static string GetAuthorizedTenant(ClaimsPrincipal? user)
    {
        // The SDK filter is the exposure gate. This recheck is the effect-time boundary directly
        // before durable work begins.
        if (user?.Identity?.IsAuthenticated != true
            || !user.HasClaim("scope", "workflow:start"))
        {
            throw new UnauthorizedAccessException("The caller is not authorized for this operation.");
        }

        return user.FindFirstValue("tenant_id")
            ?? throw new UnauthorizedAccessException("The authenticated caller has no tenant.");
    }
}

public static class WorkflowToolServerConstants
{
    public const string TaskQueue = "mcp-workflow-tool-server";
    public const string StartPolicy = "workflow:start";
}
