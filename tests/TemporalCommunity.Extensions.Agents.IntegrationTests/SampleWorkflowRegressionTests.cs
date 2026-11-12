using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SampleWorkflowRegressionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("WorkflowOrchestration", false)]
    [InlineData("WorkflowOrchestration", true)]
    [InlineData("EvaluatorOptimizer", false)]
    [InlineData("EvaluatorOptimizer", true)]
    [InlineData("MultiAgentRouting", false)]
    [InlineData("MultiAgentRouting", true)]
    [InlineData("MultiAgentParallel", false)]
    [InlineData("MultiAgentParallel", true)]
    [InlineData("WorkflowRouting", false)]
    [InlineData("WorkflowRouting", true)]
    [InlineData("DynamicRouting", false)]
    [InlineData("DynamicRouting", true)]
    [InlineData("ConfigurableAgent", false)]
    [InlineData("ConfigurableAgent", true)]
    [InlineData("PerToolActivities", false)]
    [InlineData("PerToolActivities", true)]
    [InlineData("MixedActivities", false)]
    [InlineData("MixedActivities", true)]
    public Task ActualSampleWorkflow_CompletesOrSurfacesTerminalFailure(string sample, bool fail) =>
        RunSampleAsync(sample, fail);

    [Theory]
    [InlineData("EvaluatorOptimizer", "Evaluator")]
    [InlineData("WorkflowRouting", "OrdersAgent")]
    [InlineData("DynamicRouting", "OrdersAgent")]
    [InlineData("ConfigurableAgentEscalation", "EscalationAgent")]
    public Task ActualSampleWorkflow_SurfacesTerminalFailureFromSecondAgent(string sample, string failedAgent) =>
        RunSampleAsync(sample, true, failedAgent: failedAgent);

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public Task ActualRefundWorkflow_RetriesTransientFailureWithStableTurnIdentity(string fault) =>
        RunSampleAsync("PerToolActivities", false, fault);

    [Fact]
    public Task ActualRefundWorkflow_DoesNotRetryOversizedTerminalOutcomeExceptionTransport() =>
        RunSampleAsync("PerToolActivities", true, "too-large");

    [Fact]
    public Task ActualRefundWorkflow_StopsAfterThreeRetryableInvocationFailures() =>
        RunSampleAsync("PerToolActivities", true, "always");

    private async Task RunSampleAsync(string sample, bool fail, string? fault = null, string? failedAgent = null)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var client = environment.Client;
        var queue = $"sample-regression-{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), queue);
        Directory.CreateDirectory(directory);
        output.WriteLine($"Histories: {directory}");
        var models = new Dictionary<string, IChatClient>();
        var orders = new PerToolActivities.OrderService { FailOnceEnabled = fail };
        var interceptor = new RetryProbe(fault);
        var replayOptions = new WorkflowReplayerOptions { DataConverter = TemporalAgentDataConverter.Instance };
        TemporalAgentsOptions? registration = null;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton(orders);
        var worker = builder.Services.AddHostedTemporalWorker(queue)
            .ConfigureOptions(options => options.Interceptors = [interceptor]);
        void Register<TWorkflow>()
        {
            worker.AddWorkflow<TWorkflow>();
            replayOptions.AddWorkflow<TWorkflow>();
        }
        void Model(string name, string text) =>
            models[name] = fail && (failedAgent is null || failedAgent == name)
                ? new FailingChatClient()
                : new ScriptedChatClient([new ChatResponse(new ChatMessage(ChatRole.Assistant, text))]);

        switch (sample)
        {
            case "WorkflowOrchestration":
                Register<global::WeatherOrchestrationWorkflow>();
                worker.AddScopedActivities<WorkflowOrchestration.AgentInvocationActivities>();
                Model("WeatherAssistant", "done");
                break;
            case "EvaluatorOptimizer":
                Register<EvaluatorOptimizer.EvaluatorOptimizerWorkflow>();
                worker.AddScopedActivities<EvaluatorOptimizer.AgentInvocationActivities>();
                Model("Generator", "done");
                Model("Evaluator", "APPROVED");
                break;
            case "MultiAgentRouting":
            case "MultiAgentParallel":
                Register<MultiAgentRouting.RoutingWorkflow>();
                Register<MultiAgentRouting.ParallelAgentWorkflow>();
                worker.AddScopedActivities<MultiAgentRouting.RoutingActivities>();
                Model("WeatherAgent", "done");
                Model("BillingAgent", "done");
                Model("TechSupportAgent", "done");
                break;
            case "WorkflowRouting":
            case "DynamicRouting":
                Register<WorkflowRouting.CustomerServiceWorkflow>();
                Register<WorkflowRouting.DynamicRoutingWorkflow>();
                worker.AddScopedActivities<WorkflowRouting.RoutingActivities>();
                Model("Classifier", sample == "DynamicRouting" ? "OrdersAgent" : "ORDERS");
                Model("OrdersAgent", "done");
                break;
            case "ConfigurableAgent":
            case "ConfigurableAgentEscalation":
                Register<global::SupportWorkflow>();
                worker.AddScopedActivities<global::AgentInvocationActivities>();
                Model("TriageAgent", sample == "ConfigurableAgentEscalation" ? "[ESCALATE: needs help]" : "done");
                if (sample == "ConfigurableAgentEscalation")
                    Model("EscalationAgent", "done");
                break;
            case "PerToolActivities":
                Register<PerToolActivities.RefundWorkflow>();
                worker.AddScopedActivities<PerToolActivities.RefundAgentActivities>();
                var call = new FunctionCallContent("lookup", "lookup_order",
                    new Dictionary<string, object?> { ["orderId"] = "ORD-001" });
                models["RefundAgent"] = fail
                    ? new ScriptedChatClient([new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))])
                    : ScriptedChatClient.WithToolCallsThenFinal([call], "done");
                break;
            case "MixedActivities":
                Register<MixedActivities.DocumentPipelineWorkflow>();
                worker.AddScopedActivities<MixedActivities.AgentInvocationActivities>();
                worker.AddSingletonActivities<MixedActivities.DocumentActivities>();
                Model("DocumentAnalyst", "Category: Bug\nSummary: done");
                break;
            default:
                throw new InvalidOperationException(sample);
        }
        worker.AddTemporalAgents(options =>
        {
            registration = options;
            options.EnableSearchAttributes = false;
            foreach (var (name, model) in models)
            {
                options.AddDurableAgent(name, agent =>
                {
                    agent.ChatClient = _ => model;
                    if (name == "OrdersAgent")
                        agent.Description = "Orders specialist";
                    if (name == "RefundAgent")
                        agent.AddTool("lookup_order", services => AIFunctionFactory.Create(
                            services.GetRequiredService<PerToolActivities.OrderService>().LookupOrder,
                            name: "lookup_order"));
                });
            }
        });
        using var host = builder.Build();
        await host.StartAsync();
        string? outerId = null;
        async Task<string> Run<TWorkflow, TResult>(Expression<Func<TWorkflow, Task<TResult>>> invocation)
        {
            var handle = await client.StartWorkflowAsync(invocation,
                new WorkflowOptions { Id = $"sample-{Guid.NewGuid():N}", TaskQueue = queue });
            outerId = handle.Id;
            var result = await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return result is string[] responses ? string.Join("|", responses) : result?.ToString() ?? string.Empty;
        }
        Task<string> Execute() => sample switch
        {
            "WorkflowOrchestration" => Run((global::WeatherOrchestrationWorkflow workflow) =>
                workflow.RunAsync(new global::AgentInvocationInput("weather"))),
            "EvaluatorOptimizer" => Run((EvaluatorOptimizer.EvaluatorOptimizerWorkflow workflow) => workflow.RunAsync("draft", 2)),
            "MultiAgentRouting" => Run((MultiAgentRouting.RoutingWorkflow workflow) => workflow.RunAsync("weather")),
            "MultiAgentParallel" => Run((MultiAgentRouting.ParallelAgentWorkflow workflow) => workflow.RunAsync("introduce yourself")),
            "WorkflowRouting" => Run((WorkflowRouting.CustomerServiceWorkflow workflow) => workflow.RunAsync("order")),
            "DynamicRouting" => Run((WorkflowRouting.DynamicRoutingWorkflow workflow) => workflow.RunAsync("order")),
            "ConfigurableAgent" or "ConfigurableAgentEscalation" =>
                Run((global::SupportWorkflow workflow) => workflow.RunAsync("order")),
            "PerToolActivities" => Run((PerToolActivities.RefundWorkflow workflow) =>
                workflow.RunAsync(new PerToolActivities.RefundTurnInput("order", true, null))),
            "MixedActivities" => Run((MixedActivities.DocumentPipelineWorkflow workflow) => workflow.RunAsync("doc-001")),
            _ => throw new InvalidOperationException(sample),
        };
        try
        {
            if (fail)
            {
                var failure = await Assert.ThrowsAsync<WorkflowFailedException>(Execute);
                var activity = Assert.IsType<ActivityFailureException>(failure.InnerException);
                var application = Assert.IsType<ApplicationFailureException>(activity.InnerException);
                Assert.Equal(fault switch
                {
                    "always" => nameof(InvalidOperationException),
                    "too-large" => nameof(DurableTurnOutcomeTooLargeException),
                    _ => nameof(DurableTurnFailedException),
                }, application.ErrorType);
                if (fault == "always")
                {
                    Assert.Contains("Persistent retryable invocation failure", application.Message);
                    Assert.Equal(RetryState.MaximumAttemptsReached, activity.RetryState);
                }
                if (fault == "too-large")
                    Assert.Equal(131072, Assert.IsType<ApplicationFailureException>(application.InnerException).Message.Length);
                await File.WriteAllTextAsync(Path.Combine(directory, "exception.txt"), failure.ToString());
            }
            else
            {
                Assert.Contains("done", await Execute());
            }
            if (sample == "PerToolActivities")
            {
                Assert.Equal(fault is "too-large" or "always" ? 0 : 1, orders.LookupCalls);
                Assert.Equal(fault is "too-large" or "always" ? 0 : fail ? 1 : 2,
                    ((ScriptedChatClient)models["RefundAgent"]).CallCount);
                Assert.Equal(fault == "always" ? 3 : fault is "before" or "after" ? 2 : 1,
                    interceptor.InvocationAttempts.Count);
                if (fault == "always")
                    Assert.Equal([1, 2, 3], interceptor.InvocationAttempts.Select(attempt => attempt.Attempt));
                Assert.Single(interceptor.InvocationAttempts.Select(attempt => attempt.ActivityId).Distinct());
            }
            if (failedAgent is not null)
                Assert.True(Assert.IsType<FailingChatClient>(models[failedAgent]).Calls > 0);

            if (outerId is not null)
            {
                var outer = await client.GetWorkflowHandle(outerId).FetchHistoryAsync();
                await File.WriteAllTextAsync(Path.Combine(directory, $"{outerId}.json"), outer.ToJson());
                if (fail)
                {
                    var failedActivities = outer.Events.Where(e => e.ActivityTaskFailedEventAttributes is not null).ToArray();
                    Assert.NotEmpty(failedActivities);
                    Assert.All(failedActivities, e =>
                        Assert.Equal(fault == "always" ? RetryState.MaximumAttemptsReached : RetryState.NonRetryableFailure,
                            e.ActivityTaskFailedEventAttributes.RetryState));
                }
                var sessions = new HashSet<string>();
                foreach (var scheduled in outer.Events.Select(e => e.ActivityTaskScheduledEventAttributes).Where(a => a is not null))
                {
                    if (scheduled!.Input is null)
                        continue;
                    foreach (var payload in scheduled.Input.Payloads_)
                    {
                        if (!payload.Metadata.TryGetValue("encoding", out var encoding)
                            || encoding.ToStringUtf8() != "json/plain")
                            continue;
                        using var json = JsonDocument.Parse(payload.Data.ToStringUtf8());
                        if (json.RootElement.ValueKind == JsonValueKind.String
                            && json.RootElement.GetString() is { } value && value.StartsWith("ta-", StringComparison.Ordinal))
                            sessions.Add(value);
                    }
                }
                foreach (var session in sessions.Where(_ => fault is not ("too-large" or "always")))
                {
                    var history = await client.GetWorkflowHandle(session).FetchHistoryAsync();
                    await File.WriteAllTextAsync(Path.Combine(directory, $"{session}.json"), history.ToJson());
                }
                replayOptions.AddWorkflow(AgentWorkflowWorkerFactory.CreateDefinition(
                    registration ?? throw new InvalidOperationException("Missing worker options."), queue));
                var replayer = new WorkflowReplayer(replayOptions);
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                {
                    var history = WorkflowHistory.FromJson(Path.GetFileNameWithoutExtension(path), await File.ReadAllTextAsync(path));
                    var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);
                    Assert.Null(result.ReplayFailure);
                }
            }
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class FailingChatClient() : DelegatingChatClient(new ScriptedChatClient([]))
    {
        private int _calls;
        public int Calls => _calls;

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new ApplicationFailureException("Scripted permanent model failure.", nonRetryable: true);
        }
    }

    private sealed class RetryProbe(string? fault) : IWorkerInterceptor
    {
        public ConcurrentQueue<(string ActivityId, int Attempt)> InvocationAttempts { get; } = new();
        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new Inbound(nextInterceptor, this, fault);

        private sealed class Inbound(ActivityInboundInterceptor next, RetryProbe owner, string? fault)
            : ActivityInboundInterceptor(next)
        {
            public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
            {
                var activity = ActivityExecutionContext.Current.Info;
                if (activity.ActivityType != "Run")
                    return await base.ExecuteActivityAsync(input);
                owner.InvocationAttempts.Enqueue((activity.ActivityId, activity.Attempt));
                if (fault == "always")
                    throw new InvalidOperationException("Persistent retryable invocation failure.");
                if (fault == "too-large")
                    throw new DurableTurnOutcomeTooLargeException(
                        new DurableTurnOutcomeTooLargeDetails
                        {
                            RequestId = "transport-test",
                            Status = "succeeded",
                            IsMinimal = true,
                        },
                        new InvalidOperationException(new string('x', 131072)));
                if (fault == "before" && activity.Attempt == 1)
                    throw new InvalidOperationException("Transient failure before proxy acceptance.");
                var result = await base.ExecuteActivityAsync(input);
                if (fault == "after" && activity.Attempt == 1)
                    throw new InvalidOperationException("Lost completion after successful proxy invocation.");
                return result;
            }
        }
    }
}
