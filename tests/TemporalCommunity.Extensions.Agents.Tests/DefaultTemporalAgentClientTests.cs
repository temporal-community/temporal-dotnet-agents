using FakeItEasy;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Linq.Expressions;
using Microsoft.Extensions.AI;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="DefaultTemporalAgentClient"/> guards and read-only query behavior.
/// </summary>
public class DefaultTemporalAgentClientTests
{
    private readonly ITemporalClient _fakeClient = A.Fake<ITemporalClient>();
    private readonly TemporalAgentsOptions _options = new();
    private const string TaskQueue = "test-queue";

    private DefaultTemporalAgentClient CreateClient() =>
        new(_fakeClient, _options, TaskQueue, logger: null);

    [Theory]
    [InlineData("namespace")]
    [InlineData("unknown")]
    [InlineData("no-details")]
    [InlineData("empty-details")]
    [InlineData("mixed-details")]
    [InlineData("unavailable")]
    public async Task GetTurnOutcomeAsync_UnrecognizedOrNonWorkflowFailure_Propagates(string failure)
    {
        var status = new Temporalio.Api.Common.V1.GrpcStatus();
        switch (failure)
        {
            case "namespace":
                status.Details.Add(Any.Pack(
                    new Temporalio.Api.ErrorDetails.V1.NamespaceNotFoundFailure { Namespace = "missing" }));
                break;
            case "unknown":
                status.Details.Add(Any.Pack(new StringValue { Value = "some other missing resource" }));
                break;
            case "mixed-details":
                status.Details.Add(Any.Pack(new Temporalio.Api.ErrorDetails.V1.NotFoundFailure()));
                status.Details.Add(Any.Pack(
                    new Temporalio.Api.ErrorDetails.V1.NamespaceNotFoundFailure { Namespace = "missing" }));
                break;
        }
        var exception = new RpcException(
            failure == "unavailable" ? RpcException.StatusCode.Unavailable : RpcException.StatusCode.NotFound,
            "Query failed.",
            failure == "no-details" ? null : status.ToByteArray());
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");
        A.CallTo(() => _fakeClient.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId, null, null))
            .Returns(new WorkflowHandle<AgentWorkflow>(_fakeClient, sessionId.WorkflowId));
        var interceptor = new FailingQueryInterceptor(exception);
        A.CallTo(() => _fakeClient.OutboundInterceptor).Returns(interceptor);

        var actual = await Assert.ThrowsAsync<RpcException>(() =>
            CreateClient().GetTurnOutcomeAsync(sessionId, "request"));

        Assert.Same(exception, actual);
        Assert.Equal("GetTurnOutcome", interceptor.Query?.Query);
    }

    private sealed class FailingQueryInterceptor(RpcException exception) : ClientOutboundInterceptor(null!)
    {
        public QueryWorkflowInput? Query { get; private set; }

        public override Task<TResult> QueryWorkflowAsync<TResult>(QueryWorkflowInput input)
        {
            Query = input;
            return Task.FromException<TResult>(exception);
        }
    }

    // ─── SendAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SendAsync(sessionId, null!));
    }

    // ─── RunAgentFireAndForgetAsync ──────────────────────────────────────────

    [Fact]
    public async Task RunAgentFireAndForgetAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.RunAgentFireAndForgetAsync(sessionId, null!));
    }

    [Fact]
    public async Task RunAgentFireAndForgetAsync_UsesValidatedUpdateWithStart()
    {
        _options.AddAgentProxy("Agent");
        var updateHandle = new CompletedUpdateHandle(_fakeClient);
        WorkflowStartUpdateWithStartOptions? capturedOptions = null;
        A.CallTo(() => _fakeClient.StartUpdateWithStartWorkflowAsync<AgentWorkflow>(
                A<Expression<Func<AgentWorkflow, Task>>>._,
                A<WorkflowStartUpdateWithStartOptions>._))
            .Invokes((Expression<Func<AgentWorkflow, Task>> _, WorkflowStartUpdateWithStartOptions options) =>
                capturedOptions = options)
            .Returns(Task.FromResult<WorkflowUpdateHandle>(updateHandle));
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");
        var request = new RunRequest("hello");

        await client.RunAgentFireAndForgetAsync(sessionId, request);

        Assert.NotNull(capturedOptions);
        Assert.Equal(WorkflowUpdateStage.Completed, capturedOptions!.WaitForStage);
        Assert.True(updateHandle.ResultObserved);
        Assert.NotNull(capturedOptions.StartWorkflowOperation);
        Assert.Equal(sessionId.WorkflowId, capturedOptions.StartWorkflowOperation!.Options.Id);
        A.CallTo(() => _fakeClient.GetWorkflowHandle<AgentWorkflow>(
                A<string>._,
                A<string?>._,
                A<string?>._))
            .MustNotHaveHappened();
    }

    private sealed record CompletedUpdateHandle(ITemporalClient TemporalClient)
        : WorkflowUpdateHandle(TemporalClient, "update", "workflow")
    {
        public bool ResultObserved { get; private set; }

        public override Task<TResult> GetResultAsync<TResult>(RpcOptions? rpcOptions = null)
        {
            ResultObserved = true;
            return Task.FromResult(default(TResult)!);
        }
    }

    // ─── ResolveApprovalAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ResolveApprovalAsync_NullDecision_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ResolveApprovalAsync(sessionId, null!));
    }

}
