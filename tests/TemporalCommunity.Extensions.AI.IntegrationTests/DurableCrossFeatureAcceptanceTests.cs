using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetryMetric = OpenTelemetry.Metrics.Metric;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class DurableCrossFeatureAcceptanceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private const string PrimaryProfile = "acceptance-primary";
    private const string OtherProfile = "acceptance-other";

    [Fact]
    public async Task SplitHosting_IsolatesProfile_RetriesAcrossCompactionAndCan_ApprovesAndCancelsTools_WithExportedTelemetryAndReplay()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        var clientOptions = (TemporalClientOptions)environment.Client.Options.Clone();
        clientOptions.DataConverter = DurableAIDataConverter.Instance;
        clientOptions.Interceptors =
        [
            new TracingInterceptor(new TracingInterceptorOptions
            {
                Propagator = new TraceContextPropagator(),
            }),
        ];
        var temporalClient = new TemporalClient(environment.Client.Connection, clientOptions);

        const string privatePrompt = "private-prompt-marker-acceptance";
        const string privateToolArgument = "private-tool-argument-acceptance";
        var primaryProvider = new ScriptedChatClient(
        [
            WithUsage(new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "approval-call-1",
                        "approved_tool",
                        new Dictionary<string, object?> { ["value"] = privateToolArgument }),
                ])),
            WithUsage(new ChatMessage(ChatRole.Assistant, "first turn completed")),
            WithUsage(new ChatMessage(ChatRole.Assistant, "second turn completed")),
            WithUsage(new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent("approval-call-2", "blocking_tool"),
                ])),
            WithUsage(new ChatMessage(ChatRole.Assistant, "session usable after cancellation")),
        ]);
        var otherProvider = new ScriptedChatClient([]);
        var instrumentedPrimary = new ChatClientBuilder(primaryProvider).UseOpenTelemetry().Build();
        var exportedActivities = new List<Activity>();
        var exportedMetrics = new List<OpenTelemetryMetric>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Experimental.Microsoft.Extensions.AI")
            .AddSource(DurableChatTelemetry.ActivitySourceName)
            .AddSource(TracingInterceptor.ClientSource.Name)
            .AddSource(TracingInterceptor.WorkflowsSource.Name)
            .AddSource(TracingInterceptor.ActivitiesSource.Name)
            .AddInMemoryExporter(exportedActivities)
            .Build();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("Experimental.Microsoft.Extensions.AI")
            .AddMeter(DurableChatTelemetry.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var taskQueue = $"cross-feature-acceptance-{Guid.NewGuid():N}";
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvedToolInvocations = 0;
        var blockingTool = AIFunctionFactory.Create(
            async (CancellationToken cancellationToken = default) =>
            {
                toolStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    toolCancelled.TrySetResult();
                    throw;
                }

                return (object?)"unexpected";
            },
            "blocking_tool",
            "Waits until the admitted turn is cancelled.");
        var approvedTool = AIFunctionFactory.Create(
            (string value) =>
            {
                Assert.Equal(privateToolArgument, value);
                Interlocked.Increment(ref approvedToolInvocations);
                return "approved tool result";
            },
            "approved_tool",
            "Returns an approved result.");

        var workerBuilder = Host.CreateApplicationBuilder();
        workerBuilder.Services.AddSingleton<ITemporalClient>(temporalClient);
        workerBuilder.Services.AddKeyedSingleton<IChatClient>(PrimaryProfile, instrumentedPrimary);
        workerBuilder.Services.AddKeyedSingleton<IChatClient>(OtherProfile, otherProvider);
        workerBuilder.Services.AddSingleton<IChatClient>(instrumentedPrimary);
        workerBuilder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAIWorker(options =>
            {
                options.DefaultChatClientKey = PrimaryProfile;
                options.CaptureTelemetryContent = false;
                options.ActivityTimeout = TimeSpan.FromSeconds(60);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(15);
                options.SessionTimeToLive = TimeSpan.FromMinutes(5);
                options.MaxEntryCount = 4;
                options.DefaultHistoryCompactionStrategy = DurableHistoryCompactionStrategy.TrimRecentTurns;
                options.RetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
            })
            .AddDurableTool(approvedTool, options => options.RequireApproval().NoRetry())
            .AddDurableTool(blockingTool, options => options.RequireApproval().NoRetry());
        using var workerHost = workerBuilder.Build();

        var clientServices = new ServiceCollection();
        clientServices.AddSingleton<ITemporalClient>(temporalClient);
        clientServices.AddDurableAIClient(taskQueue, options =>
        {
            options.DefaultChatClientKey = PrimaryProfile;
            options.CaptureTelemetryContent = false;
            options.MaxEntryCount = 4;
            options.DefaultHistoryCompactionStrategy = DurableHistoryCompactionStrategy.TrimRecentTurns;
            options.RetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
        });
        using var clientProvider = clientServices.BuildServiceProvider();
        var sessionClient = clientProvider.GetRequiredService<DurableChatSessionClient>();
        var conversationId = $"cross-feature-{Guid.NewGuid():N}";
        var workflowId = sessionClient.GetWorkflowId(conversationId);
        var workflow = temporalClient.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        string firstRunId;
        string secondRunId;

        await workerHost.StartAsync();
        try
        {
            var firstTurnTask = sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, privatePrompt)],
                requestId: "approved-first-turn");
            var firstApproval = await WaitForApprovalAsync(sessionClient, conversationId, firstTurnTask);
            await ResolveApprovalAsync(sessionClient, conversationId, firstApproval);
            var firstResponse = await firstTurnTask.WaitAsync(Deadline);
            Assert.Equal("first turn completed", firstResponse.Text);
            Assert.Equal(1, Volatile.Read(ref approvedToolInvocations));

            var wrongProfile = await Assert.ThrowsAsync<DurableConfigurationException>(() =>
                sessionClient.SendAsync(
                    conversationId,
                    [new ChatMessage(ChatRole.User, "must not route to the other profile")],
                    new ChatOptions().WithChatClientKey(OtherProfile),
                    requestId: "wrong-profile"));
            Assert.Contains(PrimaryProfile, wrongProfile.Message, StringComparison.Ordinal);
            Assert.Equal(0, otherProvider.CallCount);

            firstRunId = (await workflow.DescribeAsync()).RunId!;
            var secondResponse = await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "second turn")],
                requestId: "second-turn").WaitAsync(Deadline);
            Assert.Equal("second turn completed", secondResponse.Text);
            await WaitForRunChangeAsync(workflow, firstRunId);
            secondRunId = (await workflow.DescribeAsync()).RunId!;

            var retainedHistory = await sessionClient.GetHistoryAsync(conversationId);
            Assert.Contains(retainedHistory, entry => entry is TruncationMarkerEntry);
            Assert.DoesNotContain(retainedHistory, entry =>
                entry is DurableSessionRequest request && request.Messages.Any(message =>
                    message.Text.Contains(privatePrompt, StringComparison.Ordinal)));

            var modelCallsBeforeRetry = primaryProvider.CallCount;
            var retriedResponse = await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, privatePrompt)],
                requestId: "approved-first-turn").WaitAsync(Deadline);
            Assert.Equal(firstResponse.Text, retriedResponse.Text);
            Assert.Equal(modelCallsBeforeRetry, primaryProvider.CallCount);

            var cancellingTurnTask = sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "start an approved long-running tool")],
                requestId: "approved-cancelled-turn");
            var cancellationApproval = await WaitForApprovalAsync(sessionClient, conversationId, cancellingTurnTask);
            await ResolveApprovalAsync(sessionClient, conversationId, cancellationApproval);
            await toolStarted.Task.WaitAsync(Deadline);

            var cancellation = await sessionClient.CancelTurnAsync(
                conversationId,
                "approved-cancelled-turn").WaitAsync(Deadline);
            Assert.Equal(DurableTurnCancellationStatus.CancellationRequested, cancellation.Status);
            await toolCancelled.Task.WaitAsync(Deadline);
            var cancelled = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
                () => cancellingTurnTask.WaitAsync(Deadline));
            Assert.Equal(
                "DurableTurnCancelled",
                Assert.IsType<ApplicationFailureException>(cancelled.InnerException).ErrorType);

            var historyAfterCancellation = await sessionClient.GetHistoryAsync(conversationId);
            var interrupted = Assert.IsType<DurableSessionResponse>(historyAfterCancellation[^1]);
            Assert.Equal(DurableTurnCompletionReason.Interrupted, interrupted.CompletionReason);
            Assert.Contains(
                interrupted.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                result => result.CallId == "approval-call-2"
                          && result.Result?.ToString()?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true);

            var afterCancellation = await sessionClient.SendAsync(
                conversationId,
                [new ChatMessage(ChatRole.User, "continue after cancellation")],
                requestId: "turn-after-cancellation").WaitAsync(Deadline);
            Assert.Equal("session usable after cancellation", afterCancellation.Text);
            Assert.Equal(0, otherProvider.CallCount);

            Assert.True(meterProvider.ForceFlush());
            var activities = exportedActivities.ToArray();
            var inferenceActivities = activities.Where(activity =>
                Equals(activity.GetTagItem("gen_ai.operation.name"), "chat"));
            Assert.NotEmpty(inferenceActivities);
            Assert.All(
                inferenceActivities,
                activity => Assert.Equal("Experimental.Microsoft.Extensions.AI", activity.Source.Name));
            Assert.Contains(activities, activity =>
                activity.Source.Name == DurableChatTelemetry.ActivitySourceName
                && activity.DisplayName.StartsWith("execute_tool ", StringComparison.Ordinal));
            var temporalClientActivities = activities
                .Where(activity => activity.Source.Name == TracingInterceptor.ClientSource.Name)
                .ToArray();
            var temporalWorkflowActivities = activities
                .Where(activity => activity.Source.Name == TracingInterceptor.WorkflowsSource.Name)
                .ToArray();
            Assert.Contains(temporalWorkflowActivities, workflowActivity =>
                Equals(workflowActivity.GetTagItem("temporalWorkflowID"), workflowId));
            Assert.Contains(temporalClientActivities, clientActivity =>
                Equals(clientActivity.GetTagItem("temporalWorkflowID"), workflowId));
            Assert.Contains(exportedMetrics, metric =>
                metric.Name.StartsWith("gen_ai.", StringComparison.Ordinal));
            Assert.Contains(exportedMetrics, metric =>
                metric.Name == "temporal.ai.toolset.resolver.attempts");
            Assert.DoesNotContain(activities, activity =>
                ContainsSensitiveContent(activity, privatePrompt)
                || ContainsSensitiveContent(activity, privateToolArgument));

            var activityCountBeforeReplay = exportedActivities.Count;
            await ReplayRunAsync(temporalClient, workflowId, firstRunId);
            await ReplayRunAsync(temporalClient, workflowId, secondRunId);
            var currentRunId = (await workflow.DescribeAsync()).RunId!;
            await ReplayRunAsync(temporalClient, workflowId, currentRunId);
            Assert.Equal(activityCountBeforeReplay, exportedActivities.Count);
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    private static async Task<DurableApprovalRequest> WaitForApprovalAsync(
        DurableChatSessionClient sessionClient,
        string conversationId,
        Task<DurableSessionResponse> turnTask)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (turnTask.IsCompleted)
            {
                await turnTask;
                throw new InvalidOperationException("The turn completed before requesting tool approval.");
            }

            try
            {
                if (await sessionClient.GetPendingApprovalAsync(conversationId) is { } approval)
                {
                    return approval;
                }
            }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
            {
                // Update-with-start may be visible just after the caller's first query.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The approval request did not reach the split-hosted session.");
    }

    private static async Task ResolveApprovalAsync(
        DurableChatSessionClient sessionClient,
        string conversationId,
        DurableApprovalRequest approval)
    {
        await sessionClient.ResolveApprovalAsync(conversationId, new DurableApprovalDecision
        {
            RequestId = approval.RequestId,
            CallId = approval.CallId,
            FunctionName = approval.FunctionName,
            Approved = true,
        }).WaitAsync(Deadline);
    }

    private static async Task WaitForRunChangeAsync(WorkflowHandle handle, string originalRunId)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if ((await handle.DescribeAsync()).RunId is { } runId && runId != originalRunId)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The session did not continue as new after retained-history compaction.");
    }

    private static async Task ReplayRunAsync(ITemporalClient client, string workflowId, string runId)
    {
        var handle = client.GetWorkflowHandle<DurableChatWorkflow>(workflowId, runId);
        var history = await handle.FetchHistoryAsync().WaitAsync(Deadline);
        var options = new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        };
        options.AddWorkflow<DurableChatWorkflow>();
        var result = await new WorkflowReplayer(options)
            .ReplayWorkflowAsync(history, throwOnReplayFailure: false);
        Assert.Null(result.ReplayFailure);
    }

    private static bool ContainsSensitiveContent(Activity activity, string content) =>
        activity.DisplayName.Contains(content, StringComparison.Ordinal)
        || activity.Tags.Any(tag => tag.Value?.ToString()?.Contains(content, StringComparison.Ordinal) == true)
        || activity.Events.Any(activityEvent =>
            activityEvent.Tags.Any(tag => tag.Value?.ToString()?.Contains(content, StringComparison.Ordinal) == true));

    private static ChatResponse WithUsage(ChatMessage message) =>
        new(message)
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 12,
                OutputTokenCount = 8,
            },
        };
}
