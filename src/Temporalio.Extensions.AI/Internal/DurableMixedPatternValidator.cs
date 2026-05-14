using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Extensions.AI.Exceptions;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.AI.Internal;

/// <summary>
/// Startup-time A-check that detects the silent mixed-pattern conflict in MEAI:
/// <c>.UseFunctionInvocation()</c> on the <see cref="IChatClient"/> chain AND
/// <c>.AsDurable()</c>-wrapped function tools registered via <c>AddDurableTools</c>.
/// Runs as <c>IPostConfigureOptions&lt;TemporalWorkerServiceOptions&gt;</c> — the same
/// lifecycle hook used by the data-converter wiring + Step 3b's agent-pipeline validator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this check exists.</b> The two MEAI patterns are mutually exclusive:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <b>Pattern 1:</b> <c>.UseFunctionInvocation()</c> + plain
///       <see cref="AIFunction"/> tools — MEAI's <c>FunctionInvokingChatClient</c> handles
///       the function-call loop in-process inside the chat activity. Default
///       <c>DurableChatWorkflow</c> supports this.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Pattern 2:</b> <c>.AsDurable()</c>-wrapped tools + custom workflow that
///       explicitly dispatches each tool as a Temporal activity. The
///       <see cref="IChatClient"/> MUST NOT include <c>.UseFunctionInvocation()</c>, or its
///       in-process loop short-circuits the durable dispatch path.
///     </description>
///   </item>
/// </list>
/// <para>
/// When both are present, tool calls execute in-process inside the chat activity (because
/// <c>FunctionInvokingChatClient</c> intercepts them before <c>.AsDurable()</c>'s dispatch can
/// fire). Durability is silently violated. The A-check catches this combination at worker
/// startup so misconfigurations fail at boot rather than at first conversation.
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
            // No durable tools registered → no conflict possible. Pattern 1 (idiomatic
            // .UseFunctionInvocation() without .AsDurable()) remains valid.
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
            // Mixed-pattern conflict — Pattern 1 + Pattern 2 simultaneously. Throw the
            // canonical exception with its built-in remediation message.
            throw new DurableMixedPatternException();
        }
    }
}
