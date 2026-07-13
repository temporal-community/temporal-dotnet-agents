using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Startup-time validation for a durable session: when the
/// <see cref="DurableFunctionRegistry"/> is non-empty and the unkeyed
/// <see cref="IChatClient"/> chain contains <see cref="FunctionInvokingChatClient"/>, runs as
/// <c>IPostConfigureOptions&lt;TemporalWorkerServiceOptions&gt;</c> — the same lifecycle hook
/// used by the data-converter wiring and the agent-pipeline validator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this check exists.</b> The managed session owns function invocation:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <c>AddDurableTools()</c> supplies the model schemas and worker implementations.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>DurableChatWorkflow</c> dispatches every returned tool call as a Temporal activity.
///     </description>
///   </item>
/// </list>
/// <para>
/// Inline middleware would intercept tool calls before the workflow can schedule those activities.
/// This check makes that configuration fail at startup rather than silently changing behavior.
/// </para>
/// <para>
/// <b>Trigger condition:</b> <c>DurableFunctionRegistry.Count &gt; 0</c> AND the user's
/// unkeyed <see cref="IChatClient"/> chain contains <see cref="FunctionInvokingChatClient"/>.
/// </para>
/// <para>
/// <b>Failure handling per Q4:</b> if the <see cref="IChatClient"/> factory throws during
/// startup resolution (network call, missing secret), wrap the exception in
/// <see cref="DurableConfigurationException"/> with the registered chat-client key in the
/// outer message — secret content stays in the inner exception only. Host startup fails.
/// </para>
/// <para>
/// <b>Backstop:</b> the B-check at first invocation in <see cref="DurableChatActivities"/>
/// handles cases this A-check can't reach — keyed-only registrations (the A-check only walks
/// the unkeyed default), factory patterns that delay resolution until first scope, and
/// dynamically-constructed clients.
/// </para>
/// </remarks>
internal sealed class DurableMixedPatternValidator
    : IPostConfigureOptions<TemporalWorkerServiceOptions>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DurableMixedPatternValidator> _logger;

    public DurableMixedPatternValidator(
        IServiceProvider serviceProvider,
        ILoggerFactory? loggerFactory = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<DurableMixedPatternValidator>();
    }

    /// <inheritdoc/>
    public void PostConfigure(string? name, TemporalWorkerServiceOptions options)
    {
        // Detect "durable tools registered" via DurableFunctionRegistry.Count > 0.
        var registry = _serviceProvider.GetService<DurableFunctionRegistry>();
        if (registry is null || registry.Count == 0)
        {
            // No durable tools registered, so the managed-loop conflict is absent.
            return;
        }

        // Try to resolve the unkeyed IChatClient. If unregistered (user uses keyed only),
        // skip — the B-check at first invocation will catch any conflict at dispatch time.
        IChatClient? chatClient;
        try
        {
            chatClient = _serviceProvider.GetService<IChatClient>();
        }
        catch (Exception ex)
        {
            // Q4 decision: fail loudly. Wrap with a clear DurableConfigurationException so
            // the host-startup failure is diagnostically clear; the inner exception preserves
            // the original cause for further inspection.
            throw new DurableConfigurationException(
                "IChatClient resolution failed during DurableMixedPatternValidator startup check. " +
                "If the factory has transient dependencies (HTTP clients, secret-manager lookups), " +
                "consider deferring those to first use or registering a stub for startup. The " +
                "original exception is preserved as InnerException.",
                ex);
        }

        if (chatClient is null)
        {
            // No unkeyed IChatClient registered. Common for keyed-only setups. B-check will
            // cover any conflict at first invocation.
            _logger.LogDebug(
                "DurableMixedPatternValidator skipped (no unkeyed IChatClient registered). " +
                "First-invocation B-check will validate when keyed clients are resolved.");
            return;
        }

        if (AgentChainWalker.Contains<FunctionInvokingChatClient>(chatClient))
        {
            // Inline middleware conflicts with the workflow-owned tool loop.
            throw new DurableMixedPatternException();
        }
    }
}
