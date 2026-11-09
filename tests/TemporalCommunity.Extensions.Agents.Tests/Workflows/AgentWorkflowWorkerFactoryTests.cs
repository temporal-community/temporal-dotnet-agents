using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using Temporalio.Common;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public sealed class AgentWorkflowWorkerFactoryTests
{
    [Fact]
    public async Task WorkflowDefinition_MissingAgent_DefersFailureToRunAndUpdateValidation()
    {
        var input = new AgentWorkflowInput { AgentName = "MissingAgent", TaskQueue = "worker" };
        var definition = AgentWorkflowWorkerFactory.CreateDefinition(new TemporalAgentsOptions(), "worker");
        var workflow = Assert.IsType<AgentWorkflow>(definition.CreateWorkflowInstance([input]));
        var request = new RunRequest([new ChatMessage(ChatRole.User, "Hello")]);

        var runFailure = await Assert.ThrowsAsync<ApplicationFailureException>(() => workflow.RunAsync(input));
        var updateFailure = Assert.Throws<ApplicationFailureException>(() => workflow.ValidateRunAgent(request));
        var fireAndForgetFailure = Assert.Throws<ApplicationFailureException>(
            () => workflow.ValidateRunAgentFireAndForget(request));

        foreach (var failure in new[] { runFailure, updateFailure, fireAndForgetFailure })
        {
            Assert.Equal(nameof(AgentNotRegisteredException), failure.ErrorType);
            Assert.True(failure.NonRetryable);
            Assert.Contains("MissingAgent", failure.Message);
        }
    }

    [Fact]
    public void WorkflowDefinition_SuppliesWorkerSettingsBeforeWorkflowRun()
    {
        var options = new TemporalAgentsOptions
        {
            DefaultTimeToLive = TimeSpan.FromHours(6),
            DefaultActivityTimeout = TimeSpan.FromMinutes(8),
            DefaultHeartbeatTimeout = TimeSpan.FromMinutes(3),
            DefaultApprovalTimeout = TimeSpan.FromDays(2),
            DefaultRetryPolicy = new RetryPolicy { MaximumAttempts = 4 },
            DefaultMaxEntryCount = 250,
        };
        options.AddDurableAgent("Assistant", agent =>
        {
            agent.ChatClient = _ => new StubChatClient();
            agent.MaxToolCallsPerTurn = 7;
        });

        var definition = AgentWorkflowWorkerFactory.CreateDefinition(options, "worker-queue");
        var initialInput = new AgentWorkflowInput
        {
            AgentName = "Assistant",
            TaskQueue = "client-queue",
        };
        var workflow = Assert.IsType<AgentWorkflow>(
            definition.CreateWorkflowInstance([initialInput]));
        var settings = Assert.IsType<AgentWorkflowInput>(workflow.WorkerSettings);

        Assert.Equal("Assistant", settings.AgentName);
        Assert.Equal("worker-queue", settings.TaskQueue);
        Assert.Equal(TimeSpan.FromHours(6), settings.TimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(8), settings.ActivityTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), settings.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromDays(2), settings.ApprovalTimeout);
        Assert.Equal(4, settings.RetryPolicy!.MaximumAttempts);
        Assert.Equal(250, settings.MaxEntryCount);
        Assert.Equal(7, settings.MaxToolCallsPerTurn);
    }

    [Fact]
    public void WorkflowDefinition_PreservesSettingsCarriedAcrossContinueAsNew()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Assistant", agent => agent.ChatClient = _ => new StubChatClient());
        var carriedInput = DefaultTemporalAgentClient.BuildWorkerAgentWorkflowInputCore(
            "Assistant",
            options,
            "original-queue") with
        {
            ActivityTimeout = TimeSpan.FromMinutes(11),
        };

        var workflow = AgentWorkflowWorkerFactory.CreateInstance(
            [carriedInput],
            options,
            "updated-queue");

        Assert.Same(carriedInput, workflow.WorkerSettings);
        Assert.Equal(TimeSpan.FromMinutes(11), workflow.WorkerSettings!.ActivityTimeout);
        Assert.Equal("original-queue", workflow.WorkerSettings.TaskQueue);
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub");
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
