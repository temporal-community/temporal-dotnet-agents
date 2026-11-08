using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

public class ToolMiddlewareTests
{
    [Fact]
    public async Task MiddlewareUsesToolScopeAndTransformsResultWithoutResolvingModel()
    {
        var events = new List<string>();
        ScopedMarker? marker = null;
        var tool = AIFunctionFactory.Create((string value, IServiceProvider services) =>
        {
            var resolved = services.GetRequiredService<ScopedMarker>();
            Assert.Same(marker, resolved);
            events.Add("tool");
            return value;
        }, "lookup");
        using var services = BuildHarness(tool, options =>
            options.ToolMiddleware = async (agent, context, next, ct) =>
            {
                Assert.Equal("ToolAgent", agent.Name);
                Assert.Equal("lookup", context.Function.Name);
                Assert.Equal("call-1", context.CallContent.CallId);
                Assert.Equal(2, context.Iteration);
                Assert.Equal(0, context.FunctionCallIndex);
                Assert.Equal(1, context.FunctionCount);
                Assert.Single(context.Messages);
                Assert.Null(context.Options);
                Assert.False(context.IsStreaming);
                marker = context.Arguments.Services!.GetRequiredService<ScopedMarker>();
                events.Add("before");
                var result = await next(context, ct);
                events.Add("after");
                return $"decorated:{result}";
            });

        var result = await InvokeAsync(services, CreateInput());

        Assert.Equal("decorated:original", result.Result);
        Assert.Equal(["before", "tool", "after"], events);
        Assert.NotNull(marker);
        Assert.True(marker.Disposed);
    }

    [Fact]
    public async Task MiddlewareCanShortCircuitAndTerminateWithoutCallingFunction()
    {
        var calls = 0;
        var tool = AIFunctionFactory.Create(() => ++calls, "lookup");
        using var services = BuildHarness(tool, options =>
            options.ToolMiddleware = (_, context, _, _) =>
            {
                context.Terminate = true;
                return new ValueTask<object?>("cached");
            });

        var result = await InvokeAsync(services, CreateInput());

        Assert.Equal("cached", result.Result);
        Assert.True(result.Terminate);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("function")]
    [InlineData("call-id")]
    [InlineData("call-arguments")]
    [InlineData("services")]
    [InlineData("messages")]
    [InlineData("options")]
    [InlineData("streaming")]
    [InlineData("iteration")]
    public async Task ApprovedCallMutationFailsClosedOnEveryAttempt(string mutation)
    {
        var calls = 0;
        var attempts = 0;
        var tool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => ++calls, "lookup"));
        using var services = BuildHarness(tool, options =>
            options.ToolMiddleware = async (_, context, next, ct) =>
            {
                attempts++;
                switch (mutation)
                {
                    case "arguments": context.Arguments["value"] = "changed"; break;
                    case "function": context.Function = AIFunctionFactory.Create(() => ++calls, "lookup"); break;
                    case "call-id":
                        context.CallContent = new FunctionCallContent("changed", "lookup", context.Arguments);
                        break;
                    case "call-arguments": context.CallContent.Arguments!["value"] = "changed"; break;
                    case "services": context.Arguments.Services = null; break;
                    case "messages": context.Messages.Clear(); break;
                    case "options": context.Options = new ChatOptions(); break;
                    case "streaming": context.IsStreaming = true; break;
                    case "iteration": context.Iteration++; break;
                }
                return await next(context, ct);
            });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<ApplicationFailureException>(
                () => InvokeAsync(services, CreateInput(approved: true)));
            Assert.True(error.NonRetryable);
            Assert.Equal("DurableToolMiddlewareInvalid", error.ErrorType);
        }
        Assert.Equal(2, attempts);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MissingApprovalNeverEntersMiddleware()
    {
        var calls = 0;
        var attempts = 0;
        using var services = BuildHarness(
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => ++calls, "lookup")),
            options => options.ToolMiddleware = async (_, context, next, ct) =>
            {
                attempts++;
                return await next(context, ct);
            });

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => InvokeAsync(services, CreateInput()));
        Assert.Equal("DurableToolApprovalRequired", error.ErrorType);
        Assert.Equal(0, attempts);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MismatchedApprovalFingerprintFailsBeforeMiddleware()
    {
        var attempts = 0;
        using var services = BuildHarness(
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => "result", "lookup")),
            options => options.ToolMiddleware = (_, _, _, _) =>
            {
                attempts++;
                return new ValueTask<object?>("cached");
            });
        var input = CreateInput(approved: true);
        input.Arguments!["value"] = "tampered";

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => InvokeAsync(services, input));
        Assert.True(error.NonRetryable);
        Assert.Equal("DurableToolMiddlewareInvalid", error.ErrorType);
        Assert.Equal(0, attempts);
    }

    [Theory]
    [InlineData("twice")]
    [InlineData("direct-function")]
    [InlineData("inline-agent")]
    public async Task SwallowedUnsupportedInvocationStillFails(string mode)
    {
        var calls = 0;
        using var services = BuildHarness(AIFunctionFactory.Create(() => ++calls, "lookup"), options =>
            options.ToolMiddleware = async (agent, context, next, ct) =>
            {
                if (mode == "twice")
                {
                    await next(context, ct);
                }
                try
                {
                    if (mode == "direct-function")
                        await context.Function.InvokeAsync(context.Arguments, ct);
                    else if (mode == "inline-agent")
                        await agent.RunAsync("inline", cancellationToken: ct);
                    else
                        await next(context, ct);
                }
                catch (ApplicationFailureException)
                {
                }
                return "swallowed";
            });

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => InvokeAsync(services, CreateInput()));
        Assert.True(error.NonRetryable);
        Assert.Equal("DurableToolMiddlewareInvalid", error.ErrorType);
        Assert.Equal(mode == "twice" ? 1 : 0, calls);
    }

    [Fact]
    public async Task UnawaitedNextFailsAndKeepsScopeAliveUntilFunctionCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScopedMarker? marker = null;
        var calls = 0;
        var tool = AIFunctionFactory.Create(async (IServiceProvider services) =>
        {
            marker = services.GetRequiredService<ScopedMarker>();
            started.SetResult();
            await release.Task;
            Assert.False(marker.Disposed);
            calls++;
            return "done";
        }, "lookup");
        using var services = BuildHarness(tool, options =>
            options.ToolMiddleware = (agent, context, next, ct) =>
            {
                _ = next(context, ct);
                return new ValueTask<object?>("early");
            });
        var invocation = InvokeAsync(services, CreateInput());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(invocation.IsCompleted);
        release.SetResult();
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() => invocation);
        Assert.True(error.NonRetryable);
        Assert.Equal("DurableToolMiddlewareInvalid", error.ErrorType);
        Assert.Equal(1, calls);
        Assert.True(marker!.Disposed);
    }

    [Fact]
    public void ApprovalFingerprintUsesCanonicalObjectOrdering()
    {
        var first = new Dictionary<string, object?>
        {
            ["b"] = new Dictionary<string, object?> { ["x"] = 1, ["y"] = 2 },
            ["a"] = "value",
        };
        var second = new Dictionary<string, object?>
        {
            ["a"] = "value",
            ["b"] = new Dictionary<string, object?> { ["y"] = 2, ["x"] = 1 },
        };
        Assert.Equal(
            DurableToolMiddleware.Fingerprint("lookup", "call-1", first),
            DurableToolMiddleware.Fingerprint("lookup", "call-1", second));
        second["a"] = "changed";
        Assert.NotEqual(
            DurableToolMiddleware.Fingerprint("lookup", "call-1", first),
            DurableToolMiddleware.Fingerprint("lookup", "call-1", second));
    }

    [Fact]
    public async Task MiddlewareMayHandleFunctionExceptionAndReturnSerializableResult()
    {
        using var services = BuildHarness(
            AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("transient")), "lookup"),
            options => options.ToolMiddleware = async (_, context, next, ct) =>
            {
                try
                {
                    return await next(context, ct);
                }
                catch (InvalidOperationException)
                {
                    return "handled";
                }
            });

        var result = await InvokeAsync(services, CreateInput());
        Assert.Equal("handled", result.Result);
        Assert.False(result.Failed);
    }

    private static ServiceProvider BuildHarness(AIFunction tool, Action<TemporalAgentOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedMarker>();
        var options = new TemporalAgentsOptions();
        options.Agent("ToolAgent",
            _ => throw new InvalidOperationException("Tool middleware must not resolve a model client."),
            new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [tool] } },
            configure);
        services.AddSingleton(options);
        return services.BuildServiceProvider();
    }

    private static InvokeAgentToolInput CreateInput(bool approved = false)
    {
        var arguments = new Dictionary<string, object?> { ["value"] = "original" };
        return new InvokeAgentToolInput
        {
            AgentName = "ToolAgent",
            ToolName = "lookup",
            CallId = "call-1",
            Arguments = arguments,
            ApprovalAccepted = approved,
            ApprovedCallFingerprint = approved
                ? DurableToolMiddleware.Fingerprint("lookup", "call-1", arguments) : null,
            Messages = [new ChatMessage(ChatRole.User, "lookup")],
            Iteration = 2,
            FunctionCallIndex = 0,
            FunctionCount = 1,
        };
    }

    private static Task<InvokeAgentToolResult> InvokeAsync(ServiceProvider services, InvokeAgentToolInput input)
    {
        var activity = new AgentActivities(services, services.GetRequiredService<IServiceScopeFactory>());
        return new ActivityEnvironment().RunAsync(() => activity.InvokeAgentToolAsync(input));
    }

    private sealed class ScopedMarker : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }
}
