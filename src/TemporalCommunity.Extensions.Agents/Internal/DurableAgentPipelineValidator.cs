using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.Agents.Testing;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.Agents.Internal;

/// <summary>
/// Startup-time C-check validator for user-supplied agent pipelines configured via
/// <see cref="DurableAgentBuilder.ConfigureAgentPipeline"/>. Runs once per registered agent during
/// <c>IPostConfigureOptions&lt;TemporalWorkerServiceOptions&gt;</c> configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it runs at IPostConfigureOptions time.</b> The <see cref="DurableAgentBuilder.ToRegistration"/>
/// site doesn't have an <see cref="IServiceProvider"/> available. <c>IPostConfigureOptions</c> is
/// the earliest lifecycle hook with the worker's root <see cref="IServiceProvider"/> resolved —
/// the same hook the data-converter wiring uses in <see cref="TemporalAgentsRegistrar"/>. This
/// runs once at host startup before any workflow can dispatch an activity, so misconfigurations
/// fail loudly at worker boot rather than at first conversation.
/// </para>
/// <para>
/// <b>Detection strategy — two paths.</b> Per the Step 0 spike findings in
/// <c>artifacts/maf-gap-implementation-plan-v2.md</c>:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <b>Path A — Build-time pre-flight reject.</b> When the user calls
///       <c>.Use(funcInvocationCallback)</c> on the <see cref="AIAgentBuilder"/> while the inner
///       agent (<see cref="NoOpAgent"/>) does not expose a
///       <c>FunctionInvokingChatClient</c>, MAF's <see cref="AIAgentBuilder.Build"/> throws
///       <see cref="InvalidOperationException"/> with a message beginning
///       <c>"function invocation middleware can only be used with"</c>. The validator catches that
///       specific exception and translates it into
///       <see cref="DurableFunctionInvocationConflictException"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Path B — Built-chain walk.</b> If <c>Build()</c> succeeds, the resulting chain is
///       walked via <see cref="AgentChainWalker"/> for any <c>FunctionInvocationDelegatingAgent</c>
///       instance (matched by <see cref="System.Type.FullName"/> because the type is
///       <see langword="internal sealed"/> in <c>Microsoft.Agents.AI</c>). When detected, the
///       same <see cref="DurableFunctionInvocationConflictException"/> shape is thrown.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Bypass for tests.</b> When <see cref="TemporalAgentsOptions.SkipDryRunCCheck"/> is
/// <see langword="true"/>, the validator is a no-op. The runtime B-check (deferred to Step 3c)
/// inherits all enforcement responsibility in that mode.
/// </para>
/// </remarks>
internal sealed class DurableAgentPipelineValidator
    : IPostConfigureOptions<TemporalWorkerServiceOptions>
{
    /// <summary>Fully-qualified type name of MAF's internal function-invocation decorator.</summary>
    private const string FunctionInvocationDelegatingAgentFullName =
        AgentInternalConstants.FunctionInvocationDelegatingAgentFullName;

    /// <summary>Message prefix MAF emits when pre-flight rejects function-invocation middleware.</summary>
    /// <remarks>
    /// <para>
    /// Ordinal prefix match — keeps detection cheap and stable across locales.
    /// </para>
    /// <para>
    /// The full message is verified against MAF source
    /// (<c>Microsoft.Agents.AI/FunctionInvocationDelegatingAgentBuilderExtensions.cs:46</c>):
    /// "The function invocation middleware can only be used with decorations of a AIAgent
    /// that support usage of FunctionInvokingChatClient decorated chat clients."
    /// </para>
    /// </remarks>
    private const string FunctionInvocationRejectMessagePrefix =
        "The function invocation middleware can only be used with";

    private readonly TemporalAgentsOptions _agentsOptions;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableAgentPipelineValidator"/> class.
    /// </summary>
    /// <param name="agentsOptions">The shared agents options snapshot.</param>
    /// <param name="serviceProvider">
    /// The worker's root service provider. Required because MAF's <c>UseLogging()</c> decorator
    /// resolves <c>ILoggerFactory</c> from the service provider during <c>Build()</c>; passing
    /// <see langword="null"/> would cause that decorator to throw.
    /// </param>
    public DurableAgentPipelineValidator(
        TemporalAgentsOptions agentsOptions,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(agentsOptions);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _agentsOptions = agentsOptions;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public void PostConfigure(string? name, TemporalWorkerServiceOptions options)
    {
        if (_agentsOptions.SkipDryRunCCheck)
        {
            return;
        }

        foreach (var agentName in _agentsOptions.GetRegisteredAgentNames())
        {
            var registration = _agentsOptions.TryGetDurableRegistration(agentName);
            if (registration is null)
            {
                // Proxy-only declarations don't have a registration on this worker — nothing to
                // validate. The proxy's worker is responsible for its own pipeline.
                continue;
            }

            var configurePipeline = registration.ConfigureAgentPipeline
                ?? _agentsOptions.DefaultConfigureAgentPipeline;

            if (configurePipeline is null)
            {
                // No pipeline configured — nothing to validate.
                continue;
            }

            ValidateAgentPipeline(agentName, configurePipeline);
        }
    }

    /// <summary>
    /// Validates a single agent's pipeline by attempting a dry-run <c>Build()</c> and walking
    /// the resulting chain. Throws <see cref="DurableFunctionInvocationConflictException"/> on
    /// either detection path.
    /// </summary>
    /// <param name="agentName">Agent name (for diagnostic context in the thrown exception).</param>
    /// <param name="configurePipeline">User's pipeline-configuration callback.</param>
    private void ValidateAgentPipeline(
        string agentName,
        Action<AIAgentBuilder> configurePipeline)
    {
        AIAgent built;

        try
        {
            var builder = new AIAgentBuilder(NoOpAgent.Instance);
            configurePipeline.Invoke(builder);
            built = builder.Build(_serviceProvider);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.StartsWith(
                FunctionInvocationRejectMessagePrefix,
                StringComparison.Ordinal))
        {
            // Path A: MAF's own pre-flight rejected the function-invocation factory because the
            // inner agent (NoOpAgent) doesn't expose FunctionInvokingChatClient. This is exactly
            // the configuration we want to reject; translate the message into our typed
            // exception with an actionable explanation.
            throw new DurableFunctionInvocationConflictException(
                BuildConflictMessage(agentName, FunctionInvocationDelegatingAgentFullName),
                ex)
            {
                OffendingType = FunctionInvocationDelegatingAgentFullName,
            };
        }

        // Path B: walk the built chain.
        foreach (var link in AgentChainWalker.WalkAIAgent(built))
        {
            if (link.GetType().FullName == FunctionInvocationDelegatingAgentFullName)
            {
                throw new DurableFunctionInvocationConflictException(
                    BuildConflictMessage(agentName, FunctionInvocationDelegatingAgentFullName))
                {
                    OffendingType = FunctionInvocationDelegatingAgentFullName,
                };
            }
        }
    }

    private static string BuildConflictMessage(string agentName, string offendingType) =>
        $"Agent '{agentName}' has '{offendingType}' in its ConfigureAgentPipeline. " +
        "The durable agent library handles tool invocation as separate Temporal activities " +
        "(InvokeAgentTool); installing agent-side function-invocation middleware would conflict " +
        "with this contract and silently break per-tool durability. Remove the " +
        ".Use(functionInvocationCallback) / UseFunctionInvocation() call from your " +
        "ConfigureAgentPipeline configuration.";
}
