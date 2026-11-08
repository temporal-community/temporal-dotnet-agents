using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.Agents.Internal;

internal static class DurableToolMiddleware
{
    internal static string Fingerprint(
        string functionName, string? callId, IEnumerable<KeyValuePair<string, object?>>? arguments) =>
        DurableRequestFingerprint.Create(new
        {
            FunctionName = functionName,
            CallId = callId,
            Arguments = arguments?.ToDictionary(pair => pair.Key, pair => pair.Value)
                ?? new Dictionary<string, object?>(),
        });

    internal static async ValueTask<(object? Result, bool Terminate)> InvokeAsync(
        DurableAgentRegistration registration,
        InvokeAgentToolInput input,
        AIFunction function,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(input.ToolName, input.CallId, arguments);
        if (input.ApprovalAccepted
            && !string.Equals(fingerprint, input.ApprovedCallFingerprint, StringComparison.Ordinal))
        {
            throw Reject("The dispatched tool call does not match its recorded approval.");
        }

        var declaration = new MiddlewareFunction(function);
        var context = new FunctionInvocationContext
        {
            Function = declaration,
            Arguments = arguments,
            CallContent = new FunctionCallContent(input.CallId ?? string.Empty, input.ToolName, arguments),
            Messages = input.Messages?.ToList() ?? [],
            Iteration = input.Iteration,
            FunctionCallIndex = input.FunctionCallIndex,
            FunctionCount = input.FunctionCount,
        };
        var conversationFingerprint = DurableRequestFingerprint.Create(context.Messages);
        var services = arguments.Services;
        var nextCount = 0;
        var gate = new object();
        var callbackFinished = false;
        Task<object?>? invocation = null;
        ApplicationFailureException? rejection = null;
        var agent = new ToolActivityAgent(registration.Name, registration.Description);

        void Validate(FunctionInvocationContext current)
        {
            if (agent.InvocationAttempted || declaration.InvocationAttempted)
            {
                rejection = Reject("ToolMiddleware must invoke the function only through next and cannot run an agent inline.");
                throw rejection;
            }
            try
            {
                if (!ReferenceEquals(current, context)
                    || !ReferenceEquals(current.Function, declaration)
                    || !ReferenceEquals(current.Arguments.Services, services)
                    || current.IsStreaming
                    || current.Options is not null
                    || current.Iteration != input.Iteration
                    || current.FunctionCallIndex != input.FunctionCallIndex
                    || current.FunctionCount != input.FunctionCount
                    || !string.Equals(current.CallContent.Name, input.ToolName, StringComparison.Ordinal)
                    || !string.Equals(current.CallContent.CallId, input.CallId ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(Fingerprint(input.ToolName, input.CallId, current.Arguments),
                        fingerprint, StringComparison.Ordinal)
                    || !string.Equals(Fingerprint(input.ToolName, input.CallId, current.CallContent.Arguments),
                        fingerprint, StringComparison.Ordinal)
                    || !string.Equals(DurableRequestFingerprint.Create(current.Messages),
                        conversationFingerprint, StringComparison.Ordinal))
                {
                    rejection = Reject(
                        "ToolMiddleware changed the supplied invocation context. Function, call identity, " +
                        "arguments, services, messages and execution metadata must remain unchanged; " +
                        "only Terminate and the returned result may change.");
                    throw rejection;
                }
            }
            catch (DurableConfigurationException)
            {
                rejection = Reject("ToolMiddleware supplied invocation values that cannot be serialized.");
                throw rejection;
            }
        }

        async Task<object?> InvokeFunctionAsync(FunctionInvocationContext current, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, token);
            return await function.InvokeAsync(current.Arguments, linked.Token).ConfigureAwait(false);
        }

        ValueTask<object?> Next(FunctionInvocationContext current, CancellationToken token)
        {
            lock (gate)
            {
                Validate(current);
                if (callbackFinished || ++nextCount != 1)
                {
                    rejection = Reject("ToolMiddleware must await next and call it at most once per activity attempt.");
                    throw rejection;
                }
                invocation = InvokeFunctionAsync(current, token);
                return new ValueTask<object?>(invocation);
            }
        }

        object? result;
        try
        {
            result = await registration.ToolMiddleware!(
                agent, context, Next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Task<object?>? pendingInvocation = null;
            lock (gate)
            {
                callbackFinished = true;
                if (invocation is { IsCompleted: false })
                {
                    rejection = Reject("ToolMiddleware returned before its next invocation completed.");
                    pendingInvocation = invocation;
                }
            }
            if (pendingInvocation is not null)
            {
                // Keep the tool scope alive even when a callback returns or throws without awaiting next.
                await pendingInvocation.ConfigureAwait(false);
            }
        }
        if (rejection is not null)
        {
            throw rejection;
        }
        Validate(context);
        return (result, context.Terminate);
    }

    private static ApplicationFailureException Reject(string message) =>
        new(message, errorType: "DurableToolMiddlewareInvalid", nonRetryable: true);

    private sealed class MiddlewareFunction(AIFunction function) : DelegatingAIFunction(function)
    {
        internal bool InvocationAttempted { get; private set; }

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            InvocationAttempted = true;
            throw Reject("ToolMiddleware must invoke the supplied function through next.");
        }
    }

    private sealed class ToolActivityAgent(string name, string? description) : AIAgent
    {
        public override string? Name => name;
        public override string? Description => description;
        internal bool InvocationAttempted { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) => throw RejectInvocation();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) => throw RejectInvocation();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) => throw RejectInvocation();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null,
            AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw RejectInvocation();
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null,
            AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw RejectInvocation();
        }

        private ApplicationFailureException RejectInvocation()
        {
            InvocationAttempted = true;
            return Reject("ToolMiddleware receives agent metadata, not an executable agent or session owner.");
        }
    }
}
