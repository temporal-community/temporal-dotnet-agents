#pragma warning disable MAAI001 // experimental MAF AIContextProvider.InvokingContext/InvokedContext ctors; inventoried in Internal/ExperimentalApiSuppressions.cs
#pragma warning disable TA001 // IDurableToolSource is experimental; internal consumption here is intentional
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Exceptions;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using TemporalCommunity.Extensions.AI;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Immutable blueprint for a durable agent registered via <c>TemporalAgentsOptions.AddDurableAgent</c>.
/// Computed once at first activity dispatch (lazy) and reused for the lifetime of the worker.
/// Contains only things that depend on registration <em>shape</em>, not live DI instances:
/// the tool registry, frozen per-tool activity options, and the source-of-truth
/// <see cref="DurableAgentRegistration"/>. Live DI instances
/// (<see cref="IChatClient"/>, context providers, tool interceptor)
/// are resolved fresh per activity call via an <see cref="IServiceScope"/> so that scoped
/// services (e.g. <c>DbContext</c>) are never captured as implicit captive singletons.
/// </summary>
/// <param name="ToolRegistrations">Immutable registrations; implementations are resolved per activity attempt.</param>
/// <param name="Registration">Source-of-truth registration snapshot from the builder.</param>
/// <param name="AgentsOptions">Reference to the shared agents-options snapshot.</param>
internal sealed record AgentBlueprint(
    IReadOnlyDictionary<string, DurableToolRegistration> ToolRegistrations,
    DurableAgentRegistration Registration,
    TemporalAgentsOptions AgentsOptions);

/// <summary>
/// Temporal activities that perform the actual AI inference for agent sessions.
/// All AI inference must run inside an activity to preserve workflow determinism.
/// </summary>
internal sealed class AgentActivities(
    IServiceProvider services,
    IServiceScopeFactory serviceScopeFactory,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AgentActivities>();

    // Per-durable-agent blueprint cache. Computed lazily at first dispatch and reused for the
    // lifetime of the worker. Contains only frozen config (tool registry and registration shape)
    // — no live DI instances. Live instances are resolved per call via scope.
    private readonly ConcurrentDictionary<string, AgentBlueprint> _blueprintCache =
        new(StringComparer.OrdinalIgnoreCase);

    [Activity("TemporalCommunity.Extensions.Agents.InitializeChatHistoryMemory")]
    public async Task<InitializedChatHistoryMemory> InitializeChatHistoryMemoryAsync(
        InitializeChatHistoryMemoryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var blueprint = ResolveBlueprint(input.AgentName);
        var registration = blueprint.Registration.ContextProviderFactories
            .Select(provider => provider.MemorySearch)
            .SingleOrDefault(memory => memory is not null);
        if (registration is null)
        {
            return new InitializedChatHistoryMemory();
        }

        using var scope = serviceScopeFactory.CreateScope();
        var sessionId = TemporalAgentSessionId.Parse(input.SessionId);
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);
        var beforeStateBag = session.SerializeStateBag();

        var hasState = session.StateBag.TryGetValue<ChatHistoryMemoryProvider.State>(
            TemporalChatHistoryMemoryBuilder.StateKey,
            out var state);
        var hasScope = session.StateBag.TryGetValue<TemporalMemoryScopeSnapshot>(
            TemporalChatHistoryMemoryBuilder.ScopeKey,
            out var existingScope);
        if (hasScope && !hasState)
        {
            throw BuildMemorySearchConfigurationFailure(
                "The recorded chat-history memory scope exists but its provider state is missing; the initializer will not be rerun.");
        }

        if (!hasState || state is null)
        {
            if (registration.ScopeInitializer is { } initializer)
            {
                state = await initializer(
                    scope.ServiceProvider,
                    new TemporalMemoryScopeContext(
                        input.SessionId,
                        input.AgentName,
                        registration.CollectionName),
                    ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                var defaultScope = new ChatHistoryMemoryProviderScope
                {
                    AgentId = input.AgentName,
                    SessionId = input.SessionId,
                };
                state = new ChatHistoryMemoryProvider.State(defaultScope, defaultScope);
            }

            var memoryScope = TemporalMemoryScopeSnapshot.Create(
                state,
                input.AgentName,
                input.SessionId,
                registration.AllowCrossSessionRecall);
            session.StateBag.SetValue(TemporalChatHistoryMemoryBuilder.StateKey, state);
            session.StateBag.SetValue(TemporalChatHistoryMemoryBuilder.ScopeKey, memoryScope);

            var result = new InitializedChatHistoryMemory
            {
                Scope = memoryScope,
                StateBagDelta = StateBagMerge.CreateDelta(beforeStateBag, session.SerializeStateBag()),
            };
            TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                ActivityExecutionContext.Current.PayloadConverter,
                result,
                input.MaximumTransportPayloadSizeBytes,
                input.PayloadCodecExpansionAllowanceBytes,
                "chat-history memory initialization result");
            return result;
        }

        var validatedScope = TemporalMemoryScopeSnapshot.Create(
            state,
            input.AgentName,
            input.SessionId,
            registration.AllowCrossSessionRecall);
        if (!hasScope || existingScope is null || existingScope != validatedScope)
        {
            throw BuildMemorySearchConfigurationFailure(
                "The retained chat-history memory scope does not match the provider state.");
        }
        return new InitializedChatHistoryMemory { Scope = validatedScope };
    }

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the agent name when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(string? agentName) =>
        string.IsNullOrWhiteSpace(agentName) ? null : agentName;

    /// <summary>
    /// Applies a keyed history reducer to the supplied history list and returns the result.
    /// Dispatched by <see cref="AgentWorkflow"/> at continue-as-new time when
    /// <see cref="TemporalCommunity.Extensions.AI.DurableChatWorkflowInput.HistoryReducerKey"/> is set.
    /// The reducer delegate is resolved from DI via <see cref="IServiceProvider.GetKeyedService{T}"/>,
    /// applied to the history, and the trimmed list is returned to the workflow.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.Agents.ReduceHistoryByKey")]
    public Task<List<DurableSessionEntry>> ReduceHistoryByKeyAsync(
        TemporalCommunity.Extensions.AI.ReduceHistoryByKeyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var reducer = services.GetKeyedService<
            Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
            input.ReducerKey)
            ?? throw new InvalidOperationException(
                $"No history reducer registered under key '{input.ReducerKey}'. " +
                $"Register a Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> " +
                $"via services.AddKeyedSingleton(\"{input.ReducerKey}\", ...).");

        var result = reducer(input.History);
        return Task.FromResult(result as List<DurableSessionEntry> ?? result.ToList());
    }

    /// <summary>
    /// Step activity used by durable agents. Performs ONE LLM call without invoking any tools.
    /// Returns either a final assistant message or one or more <see cref="FunctionCallContent"/>
    /// items that the workflow then dispatches serially as separate
    /// <c>TemporalCommunity.Extensions.Agents.InvokeAgentTool</c> activities.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.Agents.RunDurableAgentStep")]
    public async Task<AgentStepResult> RunDurableAgentStepAsync(AgentStepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var blueprint = ResolveBlueprint(input.AgentName);
        var registration = blueprint.Registration;
        var agentsOptions = blueprint.AgentsOptions;
        // Every dispatch path reaches this boundary, including the first proxy-started step and
        // jobs without a settings handshake. Frontend snapshots are not worker policy.
        AgentModelContextBudget modelContextBudget;
        try
        {
            modelContextBudget = AgentModelContextBudget.Resolve(registration, agentsOptions);
        }
        catch (InvalidOperationException exception)
        {
            throw new ApplicationFailureException(
                exception.Message,
                exception,
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }

        // Resolve all live DI instances fresh per call from a scoped service provider.
        // The scope is disposed at the end of this activity call, ensuring scoped services
        // (e.g. DbContext) are not captured as captive singletons.
        using var scope = serviceScopeFactory.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var toolFunctions = ResolveToolFunctions(blueprint, scopedServices);
        var captureTelemetryContent =
            TemporalCommunity.Extensions.AI.Internal.DurableTelemetryContentCapturePolicy.Resolve(
                scope.ServiceProvider.GetService<TemporalCommunity.Extensions.AI.DurableExecutionOptions>());

        var chatClient = registration.ChatClient(scopedServices);
        AIContextProvider[] contextProviders = input.ProvidersPrepared
            || registration.ContextProviderFactories.Count == 0
            ? []
            : ResolveContextProviders(registration, scopedServices);

        var interceptorFactory = registration.ToolInterceptorFactory ?? agentsOptions.DefaultToolInterceptor;
        var toolInterceptor = interceptorFactory?.Invoke(scopedServices);

        var sessionId = input.SessionId ?? TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!);
        // Restore the StateBag before building the live pipeline so the innermost session boundary
        // can carry this exact durable instance through outer user middleware.
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);
        var stateBagBefore = session.SerializeStateBag();
        var turnTelemetry = new TemporalAgentTurnTelemetryContext(input.Request.CorrelationId);

        // Build and own a fresh AIAgent pipeline from the scoped IChatClient. The pipeline lease
        // is declared after the DI scope so it is disposed before scoped services at method exit.
        //
        // The catch is deliberately narrow. It covers the guard and pipeline composition — both
        // pure configuration checks whose failure can never succeed on a retry — but NOT the
        // registration.ChatClient(scopedServices) call above, which is user code that may fail
        // transiently (deferred credential acquisition, a cold dependency) and should keep its
        // normal retry behaviour.
        AgentPipelineLease pipelineLease;
        try
        {
            pipelineLease = BuildLiveAgentPipeline(
                blueprint,
                chatClient,
                session,
                turnTelemetry,
                scopedServices);
        }
        catch (DurableConfigurationException ex)
        {
            // A misconfiguration cannot be fixed by trying again. Left unwrapped it burns the
            // bounded default retry budget — or retries forever when the user set
            // MaximumAttempts = 0. The error type is the shared DurableConfigurationException
            // name because TemporalFailureInspector matches on that string.
            throw new ApplicationFailureException(
                ex.Message,
                ex,
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }

        using var agentPipeline = pipelineLease;
        var agent = agentPipeline.Agent;

        // When the workflow was started by a proxy-only client, resolve
        // and return worker-side settings so the workflow can patch its input on the first turn.
        Dictionary<string, ActivityOptions>? resolvedToolOpts = null;
        if (input.NeedsWorkerSettingsResolution)
        {
            var effectiveActivityTimeout = registration.ActivityTimeout
                ?? agentsOptions.DefaultActivityTimeout;
            var effectiveHeartbeatTimeout = registration.HeartbeatTimeout
                ?? agentsOptions.DefaultHeartbeatTimeout;
            var effectiveRetryPolicy =
                TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.ResolveForAgentTool(registration.RetryPolicy);

            resolvedToolOpts = DefaultTemporalAgentClient.BuildDurableAgentToolActivityOptions(
                registration,
                effectiveActivityTimeout,
                effectiveHeartbeatTimeout,
                effectiveRetryPolicy);
        }
        IReadOnlyList<ChatMessage> messagesForLlm = PreparedMessageAttribution.Restore(
            input.AccumulatedMessages,
            input.AccumulatedMessageAttributions);

        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = input.ProvidersPrepared
            ? input.PreparedInstructions
            : registration.Instructions;
        var availableToolFunctions = new List<AITool>();
        var preparedApprovalTools = new List<string>();
        if (input.ProvidersPrepared)
        {
            if (input.PreparedTools is null)
            {
                throw new ApplicationFailureException(
                    $"Agent '{input.AgentName}' model step is missing its prepared tool manifest.",
                    errorType: nameof(DurableConfigurationException),
                    nonRetryable: true);
            }

            foreach (var binding in input.PreparedTools)
            {
                if (!toolFunctions.TryGetValue(binding.Name, out var function))
                {
                    throw new ApplicationFailureException(
                        $"Tool '{binding.Name}' is not registered on agent '{input.AgentName}'.",
                        errorType: nameof(DurableConfigurationException),
                        nonRetryable: true);
                }

                if (!blueprint.ToolRegistrations.TryGetValue(binding.Name, out var toolRegistration)
                    || binding.IsTemporalMemorySearch != (toolRegistration.MemorySearch is not null))
                {
                    throw BuildMemorySearchConfigurationFailure(
                        $"Prepared tool '{binding.Name}' does not match the worker registration for agent '{input.AgentName}'.");
                }

                if (binding.IsFrozenProviderSelection)
                {
                    if (binding.Declaration is null)
                    {
                        throw new ApplicationFailureException(
                            $"Provider-selected tool '{binding.Name}' on agent '{input.AgentName}' has no frozen declaration.",
                            errorType: nameof(DurableConfigurationException),
                            nonRetryable: true);
                    }

                    if (binding.IsTemporalMemorySearch)
                    {
                        function = await ResolveTemporalMemorySearchFunctionAsync(
                            toolRegistration.MemorySearch!,
                            binding,
                            sessionId,
                            input.SerializedStateBag,
                            agent,
                            scopedServices,
                            ct).ConfigureAwait(false);
                    }

                    binding.Declaration.ValidateImplementation(function);
                }

                availableToolFunctions.Add(function);
                if (binding.RequiresApproval)
                {
                    preparedApprovalTools.Add(binding.Name);
                }
            }
        }
        else
        {
            availableToolFunctions.AddRange(toolFunctions
                .Where(pair => blueprint.ToolRegistrations[pair.Key].MemorySearch is null)
                .Select(pair => pair.Value));
        }

        if (input.Request.ResponseFormat is { } responseFormat)
        {
            chatOptions.ResponseFormat = responseFormat;
        }

        var contextReducerKey = registration.ContextReducerKey ?? agentsOptions.DefaultContextReducerKey;
        var currentTurnStartIndex = input.CurrentTurnStartIndex;
        IReadOnlyList<DurableChatTurnRange> previousTurnRanges = input.PreviousTurnRanges ?? [];
        if (contextReducerKey is not null)
        {
            var reducer = scopedServices.GetKeyedService<IChatReducer>(contextReducerKey)
                ?? throw new ApplicationFailureException(
                    $"No IChatReducer is registered under key '{contextReducerKey}'.",
                    errorType: "DurableContextReducerNotFound",
                    nonRetryable: true);
            try
            {
                var projection = await TemporalCommunity.Extensions.AI.Internal.DurableChatContextReducer
                    .ReduceAsync(
                        reducer,
                        messagesForLlm,
                        currentTurnStartIndex,
                        previousTurnRanges,
                        ct)
                    .ConfigureAwait(false);
                messagesForLlm = projection.Messages.ToList();
                currentTurnStartIndex = projection.CurrentTurnStartIndex;
                previousTurnRanges = projection.PreviousTurnRanges;
            }
            catch (TemporalCommunity.Extensions.AI.Internal.DurableContextReductionException exception)
            {
                throw new ApplicationFailureException(
                    exception.Message,
                    exception,
                    errorType: "DurableContextReducerInvalidOutput",
                    nonRetryable: true);
            }
        }
        messagesForLlm = FitModelContext(
            messagesForLlm,
            currentTurnStartIndex,
            previousTurnRanges,
            chatOptions,
            modelContextBudget,
            scopedServices).ToList();

        // LLM call goes through agent.RunAsync (NOT chatClient directly),
        // so any DelegatingAIAgent decorators the user added via ConfigureAgentPipeline fire
        // around the call. The agent is built fresh per call via BuildLiveAgentPipeline above.

        var workflowLocalMemorySearchTools = new List<
            (AITool Tool, TemporalMemorySearchRegistration Registration, ChatHistoryMemoryProviderScope Scope)>();
        var augmentedMessages = messagesForLlm;
        if (contextProviders.Length > 0)
        {
            // Seed the aggregated context with the current chatOptions state so providers see the
            // agent's registered instructions as the starting point — matching
            // ChatClientAgent.cs:774-779 (MAF's PrepareSessionAndMessagesAsync).
            // Tools are deliberately NOT seeded: durable tools are attached to the model call
            // separately, so aggregated.Tools starts null and anything non-null in it came from a
            // provider — which is what makes the sentinel below able to name the offender.
            // Each provider receives the PREVIOUS provider's aggregate via InvokingContext, so
            // provider N+1's *aggregate* carries provider N's contributions. What provider N+1's
            // own ProvideAIContextAsync override SEES is narrower: MAF's InvokingCoreAsync applies
            // ProvideInputMessageFilter first, which defaults to external-only and hides messages
            // earlier providers injected. Instructions are not filtered and do accumulate.
            // Chaining pattern from ChatClientAgent.cs:784 (`aiContext = await provider.InvokingAsync(...)`).
            var aggregated = new Microsoft.Agents.AI.AIContext
            {
                Messages = messagesForLlm,
                Instructions = chatOptions.Instructions,
            };

            // Track the first provider that returns tools so we can emit a single targeted warning.
            string? firstToolProviderType = null;
            int firstToolCount = 0;

            for (var providerIndex = 0; providerIndex < contextProviders.Length; providerIndex++)
            {
                var provider = contextProviders[providerIndex];
                var providerRegistration = registration.ContextProviderFactories[providerIndex];
                var previousTools = aggregated.Tools?.ToList() ?? [];
                if (providerRegistration.MemorySearch is { } memoryRegistration)
                {
                    if (input.MemoryScope is null)
                    {
                        throw BuildMemorySearchConfigurationFailure(
                            "Chat-history memory was not initialized before provider invocation.");
                    }

                    _ = GetMemorySearchScope(
                        (ChatHistoryMemoryProvider)provider,
                        session,
                        memoryRegistration,
                        input.MemoryScope);
                }

                var invokingCtx = new Microsoft.Agents.AI.AIContextProvider.InvokingContext(
                    agent, session, aggregated);
                aggregated = await provider.InvokingAsync(invokingCtx, ct).ConfigureAwait(false);

                if (providerRegistration.MemorySearch is { } bridge)
                {
                    var newTools = aggregated.Tools?
                        .Where(tool => !previousTools.Any(previous => ReferenceEquals(previous, tool)))
                        .ToList() ?? [];
                    if (bridge.SearchTime == ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling)
                    {
                        var memoryTools = newTools
                            .Where(tool => string.Equals(tool.Name, bridge.ToolName, StringComparison.Ordinal))
                            .ToList();
                        if (newTools.Count != 1 || memoryTools.Count != 1 || memoryTools[0] is not AIFunction)
                        {
                            throw BuildMemorySearchConfigurationFailure(
                                $"The Temporal memory-search provider for agent '{input.AgentName}' must return only " +
                                $"its stock on-demand function '{bridge.ToolName}'.");
                        }

                        var providerFunction = (AIFunction)memoryTools[0];
                        var memoryScope = GetMemorySearchScope(
                            (ChatHistoryMemoryProvider)provider,
                            session,
                            bridge,
                            input.MemoryScope!);
                        workflowLocalMemorySearchTools.Add((providerFunction, bridge, memoryScope));
                        availableToolFunctions.Add(providerFunction);
                    }
                    else if (newTools.Count > 0)
                    {
                        throw BuildMemorySearchConfigurationFailure(
                            "The before-model-call memory provider returned unexpected tools.");
                    }
                }

                // Strip tools from IDurableToolSource providers immediately after their iteration.
                // Their tools are already registered as durable activities; leaving them in aggregated
                // would contaminate downstream providers' InvokingContext and cause the LogError sentinel
                // to fire on the wrong provider.
                // AIContext is a sealed class (not a record), so use explicit property copy instead of `with`.
                if (provider is IDurableToolSource)
                    aggregated = new Microsoft.Agents.AI.AIContext
                    {
                        Instructions = aggregated.Instructions,
                        Messages = aggregated.Messages,
                        Tools = null,
                    };

                // Capture the first non-IDurableToolSource provider that returned tools for the warning below.
                if (firstToolProviderType is null && provider is not IDurableToolSource
                    && providerRegistration.MemorySearch is null
                    && aggregated.Tools is { } tools)
                {
                    var count = tools is ICollection<Microsoft.Extensions.AI.AITool> c ? c.Count : tools.Count();
                    if (count > 0)
                    {
                        firstToolProviderType = provider.GetType().Name;
                        firstToolCount = count;
                    }
                }
            }

            // Apply the final aggregated messages. AIContext.Messages is already materialized by
            // the last provider in the chain; no need to enumerate intermediate contexts.
            augmentedMessages = aggregated.Messages != null
                ? (aggregated.Messages as IReadOnlyList<ChatMessage> ?? aggregated.Messages.ToList())
                : messagesForLlm;

            // Apply the final aggregated instructions to chatOptions so the LLM call sees them.
            // This assignment overwrites chatOptions.Instructions, matching
            // ChatClientAgent.cs:797-801 (MAF pattern) — but it is not a replacement of the
            // agent's own instructions: those were the seed, and MAF's InvokingCoreAsync
            // concatenates each provider's addition onto the input with "\n". So the effective
            // value is "registered\nprovider-1\nprovider-2"; a provider appends, it cannot
            // override, through the default path.
            if (aggregated.Instructions is not null)
            {
                chatOptions.Instructions = aggregated.Instructions;
            }

            // Provider-contributed tools are NOT dispatched as durable activities and are ignored,
            // unless the provider implements IDurableToolSource (in which case tools were stripped
            // above and are already registered as durable activities).
            // Emit one LogError per LLM step (not per provider, not per tool) when any
            // non-IDurableToolSource provider returned tools — this is a misconfiguration: a
            // registered feature is completely non-functional until the provider is updated.
            // firstToolProviderType is a local, so a turn that takes several steps logs once per
            // step; that repetition is intentional — the condition is still true each time.
            // IDurableToolSource providers are excluded.
            if (firstToolProviderType is not null)
            {
                _logger.LogError(
                    "Context provider {ProviderType} returned {ToolCount} tool(s) for agent {AgentName}. " +
                    "Provider-contributed tools are not dispatched as durable activities and are ignored. " +
                    "To register these tools with durable execution: " +
                    "(a) implement IDurableToolSource on the provider type and use AddContextProvider(provider), or " +
                    "(b) pass tools explicitly via AddContextProvider(provider, durableTools: [new DurableToolRegistrationSpec(yourTool)]).",
                    firstToolProviderType, firstToolCount, input.AgentName);
            }
        }

        // Spread [..] makes a per-call copy so downstream mutation cannot corrupt the cached registry.
        var selectedTools = AgentRunToolSelectionPolicy.FilterProviderTools(
            availableToolFunctions,
            input.Request.EnableToolCalls,
            input.Request.EnableToolNames is { } enabledToolNames ? [.. enabledToolNames] : null);
        var selectedMemoryTools = workflowLocalMemorySearchTools
            .Where(entry => selectedTools.Any(tool => ReferenceEquals(tool, entry.Tool)))
            .Select(entry => new PreparedAgentTool
            {
                Name = entry.Registration.ToolName,
                IsFrozenProviderSelection = true,
                IsTemporalMemorySearch = true,
                MemorySearchScope = entry.Scope,
                MemoryScope = input.MemoryScope,
                Declaration = TemporalCommunity.Extensions.AI.Internal.DurableFunctionDeclarationSnapshot.Create(
                    ((AIFunction)entry.Tool).AsDeclarationOnly()),
            })
            .ToList();
        chatOptions.Tools = selectedTools.Count > 0 ? [.. selectedTools] : null;

        EnsureModelContextFits(augmentedMessages, chatOptions, modelContextBudget, scopedServices);

        var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, scopedServices);
        TemporalAgentContext.SetCurrent(temporalContext);

        // The Temporal turn span always carries Temporal-owned correlation. When upstream MAF or
        // MEAI telemetry owns canonical GenAI usage, this span omits duplicate usage attributes.
        var upstreamOwnsUsage = agentPipeline.HasOpenTelemetryAgent
            || agentPipeline.HasOpenTelemetryChatClient;
        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentTurnSpanName,
            ActivityKind.Client);

        if (span is not null)
        {
            span.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
            span.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);
            span.SetTag(TemporalAgentTelemetry.AgentCorrelationIdAttribute, input.Request.CorrelationId);
        }

        try
        {
            _logger.LogAgentActivityStarted(input.AgentName, sessionId.WorkflowId);

            // Carry the turn's ChatOptions through ChatClientAgentRunOptions so the inner
            // ChatClientAgent uses them (tools, response format, etc.). The options flow through
            // any user-installed DelegatingAIAgent decorators unchanged.
            var runOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = chatOptions,
            };

            // Outer middleware receives the exact restored TemporalAgentSession. The internal
            // innermost boundary translates to null only for ChatClientAgent, which then mints
            // the transient sealed ChatClientAgentSession required by its leaf implementation.
            var response = await RunWithHeartbeatsAsync(
                ctx,
                () => agent.RunAsync(augmentedMessages, session, runOptions, ct)).ConfigureAwait(false);
            var responseMessages = response.Messages.ToList();
            var assistantMessages = responseMessages.Count > 0
                ? responseMessages
                : [new ChatMessage(ChatRole.Assistant, string.Empty)];
            var callResolution = TemporalCommunity.Extensions.AI.Internal.DurableToolCallResolver.Resolve(
                input.AccumulatedMessages,
                responseMessages);
            if (callResolution.Failure is { } failure)
            {
                throw new ApplicationFailureException(
                    $"The model returned invalid tool-call output ({failure}); no tool was dispatched.",
                    errorType: "DurableModelOutputInvalid",
                    nonRetryable: true);
            }

            var toolCalls = callResolution.ActionableCalls;
            var approvalRequiredToolNames = toolFunctions
                .Where(pair => selectedTools.Contains(pair.Value)
                    && pair.Value.GetService<ApprovalRequiredAIFunction>() is not null)
                .Select(pair => pair.Key)
                .Concat(preparedApprovalTools)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!upstreamOwnsUsage && span?.IsAllDataRequested == true)
            {
                span.SetTag(TemporalAgentTelemetry.InputTokensAttribute, response.Usage?.InputTokenCount);
                span.SetTag(TemporalAgentTelemetry.OutputTokensAttribute, response.Usage?.OutputTokenCount);
                span.SetTag(TemporalAgentTelemetry.TotalTokensAttribute, response.Usage?.TotalTokenCount);
            }

            _logger.LogAgentActivityCompleted(input.AgentName, sessionId.WorkflowId,
                response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);

            if (contextProviders.Length > 0)
            {
                var invokedCtx = new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                    agent,
                    session,
                    requestMessages: augmentedMessages,
                    responseMessages: response.Messages);
                for (var providerIndex = 0; providerIndex < contextProviders.Length; providerIndex++)
                {
                    var provider = contextProviders[providerIndex];
                    EnsureMemoryScopeBeforeProviderHook(
                        provider,
                        registration.ContextProviderFactories[providerIndex],
                        session,
                        input.MemoryScope);
                    try
                    {
                        await provider.InvokedAsync(invokedCtx, ct).ConfigureAwait(false);
                    }
                    catch (Exception providerEx) when (
                        providerEx is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        // Contain the fault here. Letting it reach the outer catch would run the
                        // failure-path InvokedAsync loop as well, so every provider that already
                        // received the success notification would be notified a second time for
                        // the same LLM step. The model call itself succeeded and its response is
                        // already collected; a post-invocation notification failing is not a
                        // reason to discard it and pay for the call again on retry.
                        TemporalCommunity.Extensions.AI.Internal.DurableFailureLogger.LogException(
                            _logger,
                            providerEx,
                            captureTelemetryContent,
                            "AIContextProvider.InvokedAsync",
                            $"provider {provider.GetType().Name}, agent {input.AgentName}; " +
                            "step result unaffected");
                    }
                }
            }

            var stateBagDelta = StateBagMerge.CreateDelta(stateBagBefore, session.SerializeStateBag());
            var isFinal = toolCalls.Count == 0;


            int? resolvedMaxToolCalls = input.NeedsWorkerSettingsResolution
                ? registration.MaxToolCallsPerTurn
                : null;


            // Bundle resolved settings into ProxyResolvedWorkerConfig only when this was a
            // resolution-request step (NeedsWorkerSettingsResolution). Non-resolution steps return
            // null for the config; consumer forwarding properties handle the null safely.
            ProxyResolvedWorkerConfig? resolvedConfig = null;
            if (input.NeedsWorkerSettingsResolution)
            {
                // Feature L: build interceptor-related lists from tool registrations.
                ActivityOptions? interceptorActivityOpts = null;
                List<string>? interceptorSkippedTools = null;
                List<string>? requiresApprovalTools = null;

                // requiresApprovalTools is populated unconditionally — RequireApproval() is an
                // absolute floor that must be enforced even when no tool interceptor is registered
                // (BLOCK-2 fix). Only interceptorActivityOpts and the skip list need the interceptor guard.
                foreach (var toolReg in registration.Tools)
                {
                    var toolOpts = toolReg.Options;
                    if (toolOpts.RequireApprovalFlag)
                    {
                        (requiresApprovalTools ??= new List<string>()).Add(toolReg.Name);
                    }

                }

                Dictionary<string, ActivityOptions>? perToolInterceptorOpts = null;

                if (toolInterceptor is not null)
                {
                    var effectiveTimeout = registration.ActivityTimeout
                        ?? agentsOptions.DefaultActivityTimeout;
                    var effectiveHeartbeat = registration.HeartbeatTimeout
                        ?? agentsOptions.DefaultHeartbeatTimeout;
                    var effectiveRetry =
                        TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.ResolveForAgentTool(registration.RetryPolicy);

                    interceptorActivityOpts = new ActivityOptions
                    {
                        StartToCloseTimeout = effectiveTimeout,
                        HeartbeatTimeout = effectiveHeartbeat,
                        RetryPolicy = effectiveRetry,
                        // Summary is set per-tool at dispatch time: $"intercept:{toolName}"
                    };

                    foreach (var toolReg in registration.Tools)
                    {
                        if (toolReg.Options.SkipInterceptorFlag)
                        {
                            (interceptorSkippedTools ??= new List<string>()).Add(toolReg.Name);
                        }

                        // Wire per-tool interceptor timeout when set (F2 fix).
                        if (toolReg.Options.InterceptorTimeout.HasValue)
                        {
                            perToolInterceptorOpts ??= new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);
                            perToolInterceptorOpts[toolReg.Name] = new ActivityOptions
                            {
                                StartToCloseTimeout = toolReg.Options.InterceptorTimeout,
                                HeartbeatTimeout = effectiveHeartbeat,
                                RetryPolicy = effectiveRetry,
                            };
                        }
                    }
                }

                resolvedConfig = new ProxyResolvedWorkerConfig
                {
                    MaxToolCallsPerTurn = resolvedMaxToolCalls ?? registration.MaxToolCallsPerTurn,
                    ToolActivityOptions = resolvedToolOpts ?? new Dictionary<string, ActivityOptions>(),
                    InterceptorActivityOptions = interceptorActivityOpts,
                    InterceptorToolActivityOptions = perToolInterceptorOpts,
                    InterceptorSkippedTools = interceptorSkippedTools,
                    RequiresApprovalTools = requiresApprovalTools,
                };
            }

            var stepResult = new AgentStepResult
            {
                IsFinal = isFinal,
                AssistantMessages = assistantMessages,
                ToolCalls = isFinal ? null : toolCalls,
                PreparedTools = selectedMemoryTools.Count > 0 ? selectedMemoryTools : null,
                StateBagDelta = stateBagDelta,
                Usage = response.Usage,
                ResponseId = response.ResponseId,
                ResolvedWorkerConfig = resolvedConfig,
                ApprovalRequiredToolNames = approvalRequiredToolNames,
            };
            try
            {
                TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                    ctx.PayloadConverter,
                    stepResult,
                    input.MaximumTransportPayloadSizeBytes,
                    input.PayloadCodecExpansionAllowanceBytes,
                    "model-step activity result");
            }
            catch (TemporalCommunity.Extensions.AI.Exceptions.DurableTransportPayloadTooLargeException exception)
            {
                throw new ApplicationFailureException(
                    "The model response exceeded the configured durable activity result budget.",
                    exception,
                    errorType: "DurableModelResultTooLarge",
                    nonRetryable: true);
            }
            catch (ApplicationFailureException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw TemporalCommunity.Extensions.AI.Internal.DurableActivityFailurePolicy.CreateBounded(
                    exception,
                    "DurableModelResultSerializationFailed",
                    nonRetryable: true);
            }

            return stepResult;
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            TemporalCommunity.Extensions.AI.Internal.DurableFailureLogger.LogException(
                _logger,
                ex,
                captureTelemetryContent,
                "RunDurableAgentStep",
                $"agent {input.AgentName}, workflow {sessionId.WorkflowId}",
                "DurableAgentStepFailed");

            if (contextProviders.Length > 0)
            {
                var invokedCtx = new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                    agent, session, requestMessages: augmentedMessages, invokeException: ex);
                for (var providerIndex = 0; providerIndex < contextProviders.Length; providerIndex++)
                {
                    var provider = contextProviders[providerIndex];
                    EnsureMemoryScopeBeforeProviderHook(
                        provider,
                        registration.ContextProviderFactories[providerIndex],
                        session,
                        input.MemoryScope);
                    try
                    {
                        await provider.InvokedAsync(invokedCtx, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Suppressed — re-throwing the original exception below is more useful.
                    }
                }
            }

            // Retry-hardening: a deterministic LLM error (HTTP 400/401/403/404/422) never succeeds
            // on retry. Relying on an attempt cap would only delay the inevitable terminal result.
            // Rethrow as a non-retryable ApplicationFailure so Temporal stops
            // immediately; retryable/transient errors propagate unchanged for the RetryPolicy to
            // govern. Cancellation is never reclassified. Uses the same classifier + ErrorType as the
            // MEAI path (DurableChatActivities.LlmNonRetryableErrorType).
            if (ex is not OperationCanceledException
                && TemporalCommunity.Extensions.AI.Internal.LlmErrorClassifier.IsNonRetryable(ex))
            {
                throw new ApplicationFailureException(
                    $"Non-retryable LLM error: {ex.Message}",
                    ex,
                    errorType: "LlmNonRetryable",
                    nonRetryable: true);
            }

            throw;
        }
        finally
        {
            TemporalAgentContext.SetCurrent(null);
        }
    }

    [Activity("TemporalCommunity.Extensions.Agents.PrepareAgentTurn")]
    public async Task<PreparedAgentTurn> PrepareAgentTurnAsync(PrepareAgentTurnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var context = ActivityExecutionContext.Current;
        var blueprint = ResolveBlueprint(input.AgentName);
        var registration = blueprint.Registration;
        using var scope = serviceScopeFactory.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var chatClient = registration.ChatClient(scopedServices);
        var providers = registration.ContextProviderFactories.Count == 0
            ? []
            : ResolveContextProviders(registration, scopedServices);
        var toolFunctions = ResolveToolFunctions(blueprint, scopedServices);
        var registeredTools = toolFunctions
            .Where(pair => blueprint.ToolRegistrations[pair.Key].MemorySearch is null)
            .Select(pair => (Tool: new PreparationOnlyFunction(pair.Value), Name: pair.Key))
            .ToList();
        var initialTools = registeredTools.Select(registered => (AITool)registered.Tool).ToList();
        var sessionId = TemporalAgentSessionId.Parse(input.SessionId);
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);
        var turnTelemetry = new TemporalAgentTurnTelemetryContext(null);

        AgentPipelineLease pipelineLease;
        try
        {
            pipelineLease = BuildLiveAgentPipeline(
                blueprint,
                chatClient,
                session,
                turnTelemetry,
                scopedServices);
        }
        catch (DurableConfigurationException exception)
        {
            throw new ApplicationFailureException(
                exception.Message,
                exception,
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }

        using var pipeline = pipelineLease;
        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = registration.Instructions;
        var beforeStateBag = session.SerializeStateBag();
        var aggregate = new Microsoft.Agents.AI.AIContext
        {
            Instructions = chatOptions.Instructions,
            Messages = input.Messages,
            Tools = initialTools,
        };

        var memorySearchTools = new List<
            (AITool Tool, TemporalMemorySearchRegistration Registration, ChatHistoryMemoryProviderScope Scope)>();
        for (var providerIndex = 0; providerIndex < providers.Length; providerIndex++)
        {
            var provider = providers[providerIndex];
            var providerRegistration = registration.ContextProviderFactories[providerIndex];
            if (providerRegistration.MemorySearch is not null
                && provider is not ChatHistoryMemoryProvider)
            {
                throw BuildMemorySearchConfigurationFailure(
                    $"The Temporal memory-search registration for agent '{input.AgentName}' did not resolve " +
                    $"a {nameof(ChatHistoryMemoryProvider)}.");
            }

            var previousTools = aggregate.Tools?.ToList() ?? [];
            if (providerRegistration.MemorySearch is { } registeredMemory)
            {
                if (input.MemoryScope is null)
                {
                    throw BuildMemorySearchConfigurationFailure(
                        "Chat-history memory was not initialized before provider invocation.");
                }

                _ = GetMemorySearchScope(
                    (ChatHistoryMemoryProvider)provider,
                    session,
                    registeredMemory,
                    input.MemoryScope);
            }

            var invokingContext = new Microsoft.Agents.AI.AIContextProvider.InvokingContext(
                pipeline.Agent,
                session,
                aggregate);
            aggregate = await provider.InvokingAsync(invokingContext, context.CancellationToken)
                .ConfigureAwait(false);

            if (registeredTools.Any(registered => registered.Tool.InvocationAttempted))
            {
                throw PreparationOnlyFunction.CreateFailure();
            }

            if (providerRegistration.MemorySearch is { } bridge)
            {
                var providerTools = aggregate.Tools?
                    .Where(tool => !previousTools.Any(previous => ReferenceEquals(previous, tool)))
                    .ToList() ?? [];
                var memoryTools = providerTools
                    .Where(tool => string.Equals(tool.Name, bridge.ToolName, StringComparison.Ordinal))
                    .ToList();
                if (bridge.SearchTime == ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling
                    && (providerTools.Count != 1 || memoryTools.Count != 1 || memoryTools[0] is not AIFunction))
                {
                    throw BuildMemorySearchConfigurationFailure(
                        $"The Temporal memory-search provider for agent '{input.AgentName}' must return only " +
                        $"its stock on-demand function '{bridge.ToolName}'.");
                }
                if (bridge.SearchTime != ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling
                    && providerTools.Count != 0)
                {
                    throw BuildMemorySearchConfigurationFailure(
                        "The before-model-call memory provider returned unexpected tools.");
                }

                var initializedScope = input.MemoryScope
                    ?? throw BuildMemorySearchConfigurationFailure(
                        "Chat-history memory was not initialized before provider invocation.");
                var memorySearchScope = GetMemorySearchScope(
                    (ChatHistoryMemoryProvider)provider,
                    session,
                    bridge,
                    initializedScope);
                if (bridge.SearchTime == ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling)
                {
                    memorySearchTools.Add((memoryTools[0], bridge, memorySearchScope));
                }
            }
        }

        var effectiveTools = aggregate.Tools?.ToList() ?? [];
        var providerChangedToolSet = effectiveTools.Count != initialTools.Count
            || effectiveTools.Where((tool, index) => !ReferenceEquals(tool, initialTools[index])).Any();
        var preparedTools = new List<PreparedAgentTool>(effectiveTools.Count);
        var seenToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in effectiveTools)
        {
            var registeredToolIndex = registeredTools.FindIndex(
                registered => ReferenceEquals(registered.Tool, tool));
            if (registeredToolIndex < 0)
            {
                var memorySearchIndex = memorySearchTools.FindIndex(
                    entry => ReferenceEquals(entry.Tool, tool));
                if (memorySearchIndex >= 0
                    && tool is AIFunction memorySearchFunction
                    && blueprint.ToolRegistrations.TryGetValue(
                        memorySearchTools[memorySearchIndex].Registration.ToolName,
                        out var memoryToolRegistration))
                {
                    var memorySearch = memorySearchTools[memorySearchIndex];
                    preparedTools.Add(new PreparedAgentTool
                    {
                        Name = memorySearch.Registration.ToolName,
                        IsFrozenProviderSelection = true,
                        Declaration = TemporalCommunity.Extensions.AI.Internal.DurableFunctionDeclarationSnapshot.Create(
                            memorySearchFunction.AsDeclarationOnly()),
                        RequiresApproval = memoryToolRegistration.Options.RequireApprovalFlag
                            || memorySearchFunction.GetService<ApprovalRequiredAIFunction>() is not null,
                        IsTemporalMemorySearch = true,
                        MemorySearchScope = memorySearch.Scope,
                        MemoryScope = input.MemoryScope,
                    });
                    continue;
                }

                throw new ApplicationFailureException(
                    $"Context preparation for agent '{input.AgentName}' returned tool '{tool.Name}' that is not " +
                    "the exact function instance seeded for durable execution. Provider-created tools, " +
                    "wrappers, and same-name/schema substitutes are not eligible.",
                    errorType: nameof(DurableConfigurationException),
                    nonRetryable: true);
            }

            var name = registeredTools[registeredToolIndex].Name;
            if (!seenToolNames.Add(name))
            {
                throw new ApplicationFailureException(
                    $"Context preparation for agent '{input.AgentName}' returned the registered tool '{name}' more than once.",
                    errorType: nameof(DurableConfigurationException),
                    nonRetryable: true);
            }

            var toolRegistration = blueprint.ToolRegistrations[name];
            preparedTools.Add(new PreparedAgentTool
            {
                Name = name,
                IsFrozenProviderSelection = providerChangedToolSet,
                Declaration = providerChangedToolSet
                    ? TemporalCommunity.Extensions.AI.Internal.DurableFunctionDeclarationSnapshot.Create(
                        ((AIFunction)tool).AsDeclarationOnly())
                    : null,
                RequiresApproval = toolRegistration.Options.RequireApprovalFlag
                    || tool.GetService<ApprovalRequiredAIFunction>() is not null,
            });
        }

        var preparedMessages = aggregate.Messages?.ToList() ?? input.Messages.ToList();
        var prepared = new PreparedAgentTurn
        {
            Messages = preparedMessages,
            MessageAttributions = PreparedMessageAttribution.Capture(preparedMessages),
            Instructions = aggregate.Instructions,
            Tools = preparedTools,
            HasToolMiddleware = registration.ToolMiddleware is not null,
            StateBagDelta = StateBagMerge.CreateDelta(beforeStateBag, session.SerializeStateBag()),
        };
        TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
            context.PayloadConverter,
            prepared,
            input.MaximumTransportPayloadSizeBytes,
            input.PayloadCodecExpansionAllowanceBytes,
            "agent-turn context preparation result");
        return prepared;
    }

    private sealed class PreparationOnlyFunction(AIFunction innerFunction) : DelegatingAIFunction(innerFunction)
    {
        internal bool InvocationAttempted { get; private set; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            InvocationAttempted = true;
            throw CreateFailure();
        }

        internal static ApplicationFailureException CreateFailure() =>
            new(
                "Context providers may select seeded tools, but cannot invoke them during preparation. " +
                "Tools execute only after workflow dispatch and approval.",
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
    }

    [Activity("TemporalCommunity.Extensions.Agents.CompleteAgentTurn")]
    public async Task<CompletedAgentTurn> CompleteAgentTurnAsync(CompleteAgentTurnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var context = ActivityExecutionContext.Current;
        var blueprint = ResolveBlueprint(input.AgentName);
        var registration = blueprint.Registration;
        using var scope = serviceScopeFactory.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var chatClient = registration.ChatClient(scopedServices);
        var providers = registration.ContextProviderFactories.Count == 0
            ? []
            : ResolveContextProviders(registration, scopedServices);
        var sessionId = TemporalAgentSessionId.Parse(input.SessionId);
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);
        var turnTelemetry = new TemporalAgentTurnTelemetryContext(null);
        using var pipeline = BuildLiveAgentPipeline(
            blueprint,
            chatClient,
            session,
            turnTelemetry,
            scopedServices);

        TemporalAgentContext.SetCurrent(
            new TemporalAgentContext(context.TemporalClient, session, scopedServices));
        try
        {
            var beforeStateBag = session.SerializeStateBag();
            Exception? invokeException = input.InvokeExceptionMessage is null
                ? null
                : input.IsCancellation
                    ? new OperationCanceledException(input.InvokeExceptionMessage)
                    : new InvalidOperationException(input.InvokeExceptionMessage);

            for (var providerIndex = 0; providerIndex < providers.Length; providerIndex++)
            {
                var provider = providers[providerIndex];
                EnsureMemoryScopeBeforeProviderHook(
                    provider,
                    registration.ContextProviderFactories[providerIndex],
                    session,
                    input.MemoryScope);

                var stateBeforeProvider = session.SerializeStateBag();
                var invokedContext = invokeException is null
                    ? new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                        pipeline.Agent,
                        session,
                        input.RequestMessages,
                        input.ResponseMessages)
                    : new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                        pipeline.Agent,
                        session,
                        input.RequestMessages,
                        invokeException);
                try
                {
                    await provider.InvokedAsync(invokedContext, context.CancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
                {
                    session.RestoreStateBag(stateBeforeProvider);
                    TemporalCommunity.Extensions.AI.Internal.DurableFailureLogger.LogException(
                        _logger,
                        exception,
                        TemporalCommunity.Extensions.AI.Internal.DurableTelemetryContentCapturePolicy.Resolve(
                            scopedServices.GetService<TemporalCommunity.Extensions.AI.DurableExecutionOptions>()),
                        "AIContextProvider.InvokedAsync",
                        $"provider {provider.GetType().Name}, agent {input.AgentName}; turn result unaffected");
                }
            }

            var result = new CompletedAgentTurn
            {
                StateBagDelta = StateBagMerge.CreateDelta(beforeStateBag, session.SerializeStateBag()),
            };
            TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                context.PayloadConverter,
                result,
                input.MaximumTransportPayloadSizeBytes,
                input.PayloadCodecExpansionAllowanceBytes,
                "agent-turn completion result");
            return result;
        }
        finally
        {
            TemporalAgentContext.SetCurrent(null);
        }
    }

    /// <summary>
    /// Resolves (and lazily builds) the immutable blueprint for a durable agent.
    /// The blueprint contains only frozen config — live DI instances are resolved per call.
    /// </summary>
    /// <exception cref="AgentNotRegisteredException">
    /// Thrown when no durable agent with this name is registered.
    /// </exception>
    internal AgentBlueprint ResolveBlueprint(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _blueprintCache.GetOrAdd(name, static (n, ctx) =>
        {
            var (self, providerServices) = ctx;
            return self.BuildAgentBlueprint(n, providerServices);
        }, (this, services));
    }

    private static async Task<T> RunWithHeartbeatsAsync<T>(
        ActivityExecutionContext context,
        Func<Task<T>> operation)
    {
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken);
        var heartbeatTask = Task.Run(() => HeartbeatAsync(context, heartbeatStop.Token));
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeatTask.ConfigureAwait(false);
        }
    }

    private static async Task HeartbeatAsync(
        ActivityExecutionContext context,
        CancellationToken stopToken)
    {
        var interval = context.Info.HeartbeatTimeout is { } timeout && timeout > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Max(
                TimeSpan.TicksPerMillisecond,
                Math.Min(timeout.Ticks / 2, TimeSpan.TicksPerSecond)))
            : TimeSpan.FromSeconds(1);
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                context.Heartbeat();
                await Task.Delay(interval, stopToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Builds an <see cref="AgentBlueprint"/> for the named agent. Called at most once per agent
    /// name (per worker lifetime) — subsequent calls reuse the cached blueprint.
    /// Pipeline construction and structural checks occur at startup and once per live activity
    /// attempt; blueprint creation does not construct middleware or resolve a chat client.
    /// </summary>
    private AgentBlueprint BuildAgentBlueprint(string name, IServiceProvider providerServices)
    {
        var agentsOptions = providerServices.GetService<TemporalAgentsOptions>()
            ?? throw new InvalidOperationException(
                "TemporalAgentsOptions is not registered in DI. Call AddTemporalAgents on the worker " +
                "builder before invoking the durable-agent dispatch path.");

        if (!agentsOptions.DurableAgentRegistrations.TryGetValue(name, out var registration))
        {
            throw new AgentNotRegisteredException(name);
        }

        var toolRegistrations = registration.Tools.ToDictionary(
            tool => tool.Name,
            StringComparer.OrdinalIgnoreCase);
        return new AgentBlueprint(
            toolRegistrations,
            registration,
            agentsOptions);
    }

    private IReadOnlyDictionary<string, AIFunction> ResolveToolFunctions(
        AgentBlueprint blueprint,
        IServiceProvider scopedServices)
    {
        var resolvedTools = new Dictionary<string, AIFunction>(
            blueprint.ToolRegistrations.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (name, registration) in blueprint.ToolRegistrations)
        {
            resolvedTools.Add(name, ResolveToolFunction(registration, scopedServices, blueprint.Registration.Name));
        }

        return resolvedTools;
    }

    internal static async Task<AIFunction> ResolveTemporalMemorySearchFunctionAsync(
        TemporalMemorySearchRegistration registration,
        PreparedAgentTool preparedTool,
        TemporalAgentSessionId sessionId,
        System.Text.Json.JsonElement? serializedStateBag,
        AIAgent agent,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        if (!preparedTool.IsTemporalMemorySearch
            || !string.Equals(preparedTool.Name, registration.ToolName, StringComparison.Ordinal)
            || preparedTool.MemorySearchScope is null
            || preparedTool.MemoryScope is null
            || preparedTool.Declaration is null)
        {
            throw BuildMemorySearchConfigurationFailure(
                $"The prepared memory-search binding '{preparedTool.Name}' does not match the worker registration.");
        }

        var provider = registration.ProviderFactory(scopedServices);
        var isolatedSession = TemporalAgentSession.FromStateBag(sessionId, serializedStateBag);
        var validatedScope = GetMemorySearchScope(
            provider,
            isolatedSession,
            registration,
            preparedTool.MemoryScope);
        if (!string.Equals(
                validatedScope.SessionId,
                preparedTool.MemorySearchScope.SessionId,
                StringComparison.Ordinal)
            || !string.Equals(
                validatedScope.AgentId,
                preparedTool.MemorySearchScope.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                validatedScope.ApplicationId,
                preparedTool.MemorySearchScope.ApplicationId,
                StringComparison.Ordinal)
            || !string.Equals(
                validatedScope.UserId,
                preparedTool.MemorySearchScope.UserId,
                StringComparison.Ordinal))
        {
            throw BuildMemorySearchConfigurationFailure(
                "The prepared memory-search scope does not match the retained workflow-owned scope.");
        }

        var invokingContext = new Microsoft.Agents.AI.AIContextProvider.InvokingContext(
            agent,
            isolatedSession,
            new Microsoft.Agents.AI.AIContext { Messages = [] });
        var context = await provider.InvokingAsync(invokingContext, cancellationToken)
            .ConfigureAwait(false);
        var functions = context.Tools?.OfType<AIFunction>().ToList() ?? [];
        if (context.Tools?.Count() != 1
            || functions.Count != 1
            || !string.Equals(functions[0].Name, registration.ToolName, StringComparison.Ordinal))
        {
            throw BuildMemorySearchConfigurationFailure(
                $"The registered ChatHistoryMemoryProvider did not rebuild the stock search function '{registration.ToolName}'.");
        }

        preparedTool.Declaration.ValidateImplementation(functions[0]);
        return functions[0];
    }

    private static ChatHistoryMemoryProviderScope GetMemorySearchScope(
        ChatHistoryMemoryProvider provider,
        TemporalAgentSession session,
        TemporalMemorySearchRegistration registration,
        TemporalMemoryScopeSnapshot expectedScope)
    {
        if (provider.StateKeys.Count != 1
            || !string.Equals(
                provider.StateKeys[0],
                TemporalChatHistoryMemoryBuilder.StateKey,
                StringComparison.Ordinal))
        {
            throw BuildMemorySearchConfigurationFailure(
                "The ChatHistoryMemoryProvider must retain the library-owned state key for its durable bridge.");
        }

        var stateKey = provider.StateKeys[0];
        if (!session.StateBag.TryGetValue<ChatHistoryMemoryProvider.State>(stateKey, out var state)
            || state is null)
        {
            throw BuildMemorySearchConfigurationFailure(
                $"The retained ChatHistoryMemoryProvider state '{stateKey}' is missing; the memory initializer will not be rerun.");
        }

        if (!session.StateBag.TryGetValue<TemporalMemoryScopeSnapshot>(
                TemporalChatHistoryMemoryBuilder.ScopeKey,
                out var recordedScope)
            || recordedScope is null
            || recordedScope != expectedScope
            || !expectedScope.Matches(state))
        {
            throw BuildMemorySearchConfigurationFailure(
                "The retained ChatHistoryMemoryProvider scope does not match the initialized workflow-owned scope.");
        }

        return new ChatHistoryMemoryProviderScope(state.SearchScope);
    }

    private static void EnsureMemoryScopeBeforeProviderHook(
        AIContextProvider provider,
        ContextProviderRegistration providerRegistration,
        TemporalAgentSession session,
        TemporalMemoryScopeSnapshot? memoryScope)
    {
        if (providerRegistration.MemorySearch is not { } registration)
        {
            return;
        }

        if (memoryScope is null)
        {
            throw BuildMemorySearchConfigurationFailure(
                "Chat-history memory scope is missing before a provider hook.");
        }

        if (provider is not ChatHistoryMemoryProvider memoryProvider)
        {
            throw BuildMemorySearchConfigurationFailure(
                "The registered chat-history memory provider could not be resolved before a provider hook.");
        }

        _ = GetMemorySearchScope(memoryProvider, session, registration, memoryScope);
    }

    private static ApplicationFailureException BuildMemorySearchConfigurationFailure(string message) =>
        new(
            message,
            errorType: nameof(DurableConfigurationException),
            nonRetryable: true);

    // ── Per-call helper: build and own a live AIAgent pipeline ───────────────────────────────
    /// <summary>
    /// Resolves each registered context provider from the attempt's scope, rejecting a
    /// factory-registered <see cref="IDurableToolSource"/> whose declarations were never
    /// registered.
    /// </summary>
    /// <remarks>
    /// Declarations can only be collected on the instance path, where the provider exists at build
    /// time. A factory-resolved source would otherwise return functions that have no registered
    /// durable activity route. Fail before model dispatch; no number of retries turns an
    /// unregistered declaration into a registered one.
    /// </remarks>
    private static AIContextProvider[] ResolveContextProviders(
        DurableAgentRegistration registration,
        IServiceProvider scopedServices)
    {
        var resolved = new AIContextProvider[registration.ContextProviderFactories.Count];

        for (var i = 0; i < resolved.Length; i++)
        {
            var entry = registration.ContextProviderFactories[i];
            var provider = entry.Factory(scopedServices);

            if (provider is ChatHistoryMemoryProvider && entry.MemorySearch is null)
            {
                throw BuildMemorySearchConfigurationFailure(
                    $"Agent '{registration.Name}' registered {nameof(ChatHistoryMemoryProvider)} through " +
                    "the generic context-provider path. Register it with " +
                    $"{nameof(TemporalAgentOptions)}.{nameof(TemporalAgentOptions.UseChatHistoryMemory)} " +
                    "so its scope is initialized and validated before vector-store access.");
            }

            if (entry.MemorySearch is not null && provider is not ChatHistoryMemoryProvider)
            {
                throw BuildMemorySearchConfigurationFailure(
                    $"The Temporal memory-search registration for agent '{registration.Name}' did not resolve " +
                    $"a {nameof(ChatHistoryMemoryProvider)}.");
            }

            if (!entry.DeclarationsRegistered && provider is IDurableToolSource)
            {
                var conflict = new DurableConfigurationException(
                    $"Agent '{registration.Name}' registered '{provider.GetType().Name}' through the " +
                    "AddContextProvider(Func<IServiceProvider, AIContextProvider>) overload, but it " +
                    "implements IDurableToolSource. Declarations can only be collected when the " +
                    "provider instance is available at build time. Register it as an instance " +
                    "(AddContextProvider(provider)) or pass its tools explicitly via " +
                    "or pass its tools explicitly via AddContextProvider(provider, durableTools: [...]).");

                throw new ApplicationFailureException(
                    conflict.Message,
                    conflict,
                    errorType: nameof(DurableConfigurationException),
                    nonRetryable: true);
            }

            resolved[i] = provider;
        }

        return resolved;
    }

    private static IList<ChatMessage> FitModelContext(
        IReadOnlyList<ChatMessage> messages,
        int currentTurnStartIndex,
        IReadOnlyList<DurableChatTurnRange> previousTurnRanges,
        ChatOptions options,
        AgentModelContextBudget budget,
        IServiceProvider scopedServices)
    {
        try
        {
            return TemporalCommunity.Extensions.AI.Internal.DurableModelContextBudget.Fit(
                messages,
                currentTurnStartIndex,
                previousTurnRanges,
                options,
                budget.MaximumTokens,
                budget.OutputTokenReserve,
                scopedServices.GetService<IDurableChatTokenEstimator>());
        }
        catch (DurableContextBudgetExceededException exception)
        {
            throw CreateContextBudgetFailure(exception);
        }
    }

    private static void EnsureModelContextFits(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        AgentModelContextBudget budget,
        IServiceProvider scopedServices)
    {
        try
        {
            TemporalCommunity.Extensions.AI.Internal.DurableModelContextBudget.EnsureFits(
                messages,
                options,
                budget.MaximumTokens,
                budget.OutputTokenReserve,
                scopedServices.GetService<IDurableChatTokenEstimator>());
        }
        catch (DurableContextBudgetExceededException exception)
        {
            throw CreateContextBudgetFailure(exception);
        }
    }

    private static ApplicationFailureException CreateContextBudgetFailure(
        DurableContextBudgetExceededException exception) =>
        new(
            exception.Message,
            exception,
            errorType: "DurableContextBudgetExceeded",
            nonRetryable: true);

    private AgentPipelineLease BuildLiveAgentPipeline(
        AgentBlueprint blueprint,
        IChatClient chatClient,
        TemporalAgentSession session,
        TemporalAgentTurnTelemetryContext turnTelemetry,
        IServiceProvider scopedServices)
    {
        var registration = blueprint.Registration;
        var agentsOptions = blueprint.AgentsOptions;

        // Reject an in-process function-invocation loop before anything is constructed, so no
        // pipeline lease exists to unwind and no model or tool call has happened yet. This is the
        // authoritative check: the factory runs per activity attempt and may return a different
        // client each time, so it cannot be settled at startup.
        DurableFunctionInvocationGuard.ThrowIfChatClientInvokesFunctions(registration.Name, chatClient);

        var agentOptions = new ChatClientAgentOptions
        {
            Name = registration.Name,
            Description = registration.Description,
            // The activity supplies the complete effective ChatOptions on every run. Leaving
            // defaults here would make MAF merge registered tools/instructions/stop sequences
            // back into the filtered per-run options.
            ChatOptions = null,
            // Q1 (β): keep our explicit AIContextProvider loop inside RunDurableAgentStepAsync
            // rather than delegating to MAF's ChatClientAgent. Setting null here suppresses MAF's
            // own provider lifecycle so providers fire once per LLM step from our loop.
            AIContextProviders = null,
            UseProvidedChatClientAsIs = true,
        };

        var chatClientAgent = new ChatClientAgent(chatClient, agentOptions);

        var configurePipeline = registration.ConfigureAgentPipeline
            ?? agentsOptions.DefaultConfigureAgentPipeline;

        AgentPipelineLease pipeline;
        try
        {
            pipeline = AgentPipelineComposer.Compose(
                registration.Name,
                chatClientAgent,
                configurePipeline,
                scopedServices,
                builder => builder.Use(inner =>
                    new TemporalSessionBoundaryAgent(inner, session, turnTelemetry)),
                chatClient);
            turnTelemetry.EnrichMafInvokeAgentSpan = pipeline.HasOpenTelemetryAgent;
        }
        catch (InvalidOperationException ex)
            when (ex.Message.StartsWith(
                FunctionInvocationRejectMessagePrefix,
                StringComparison.Ordinal))
        {
            throw BuildFunctionInvocationConflictException(registration.Name, ex);
        }

        foreach (var link in AgentChainWalker.WalkAIAgent(pipeline.Agent))
        {
            if (link.GetType().FullName == FunctionInvocationDelegatingAgentFullName)
            {
                pipeline.Dispose();
                throw BuildFunctionInvocationConflictException(registration.Name);
            }
        }

        return pipeline;
    }

    private static DurableFunctionInvocationConflictException BuildFunctionInvocationConflictException(
        string agentName,
        Exception? innerException = null)
    {
        var message =
            $"Agent '{agentName}' has '{FunctionInvocationDelegatingAgentFullName}' in its pipeline. " +
            "The durable agent library handles tool invocation as separate Temporal activities " +
            "(InvokeAgentTool); installing agent-side function-invocation middleware would " +
            "conflict with this contract and silently break per-tool durability. Remove the " +
            ".Use(functionInvocationCallback) / UseFunctionInvocation() call from your " +
            "ConfigureAgentPipeline configuration. Use TemporalAgentOptions.ToolMiddleware " +
            "for callbacks inside the durable tool activity.";
        return innerException is null
            ? new DurableFunctionInvocationConflictException(message)
            {
                OffendingType = FunctionInvocationDelegatingAgentFullName,
            }
            : new DurableFunctionInvocationConflictException(message, innerException)
            {
                OffendingType = FunctionInvocationDelegatingAgentFullName,
            };
    }

    /// <summary>Fully-qualified type name of MAF's internal function-invocation decorator.</summary>
    private const string FunctionInvocationDelegatingAgentFullName =
        Internal.AgentInternalConstants.FunctionInvocationDelegatingAgentFullName;

    private const string FunctionInvocationRejectMessagePrefix =
        "The function invocation middleware can only be used with";

    /// <summary>
    /// Pre-tool lifecycle activity. Fires before <c>InvokeAgentTool</c> for each tool call in a
    /// turn. Resolves the agent's <see cref="IAgentToolInterceptor"/> (per-agent or worker default),
    /// calls <see cref="IAgentToolInterceptor.BeforeToolCallAsync"/>, and returns a serializable
    /// <see cref="DurableToolInterceptorResult"/> DTO for the workflow to act on.
    /// </summary>
    /// <remarks>
    /// Missing interceptors return Proceed. The workflow independently enforces each tool's
    /// RequireApproval registration, including when the interceptor is missing or skipped.
    /// </remarks>
    [Activity("TemporalCommunity.Extensions.Agents.RunToolInterceptor")]
    public async Task<DurableToolInterceptorResult> RunToolInterceptorAsync(DurableToolInterceptorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var blueprint = ResolveBlueprint(input.AgentName);

        // Resolve tool interceptor fresh per call from a scoped service provider.
        using var scope = serviceScopeFactory.CreateScope();
        var interceptorFactory = blueprint.Registration.ToolInterceptorFactory
            ?? blueprint.AgentsOptions.DefaultToolInterceptor;
        var toolInterceptor = interceptorFactory?.Invoke(scope.ServiceProvider);

        if (toolInterceptor is null)
        {
            // Interceptor was removed between workflow dispatch and activity execution
            // (e.g. worker restart without interceptor re-registration).
            _logger.LogWarning(
                "RunToolInterceptor dispatched for agent '{AgentName}' tool '{ToolName}' " +
                "but no IAgentToolInterceptor is resolved. Defaulting to Proceed.",
                input.AgentName, input.ToolName);
            return new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed };
        }

        ctx.Heartbeat();

        // Deserialize the state bag snapshot when present. Parsing the workflow ID is only
        // valid when this activity runs inside AgentWorkflow (ID = ta-{agent}-{key} format).
        // Skip parsing when there is nothing to deserialize; non-session activity invocations
        // do not have a managed agent session ID.
        AgentSessionStateBag? stateBag = null;
        if (input.SerializedStateBag.HasValue)
        {
            var session = TemporalAgentSession.FromStateBag(
                TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!),
                input.SerializedStateBag);
            stateBag = session.StateBag.Count > 0 ? session.StateBag : null;
        }

        var toolContext = new AgentToolContext
        {
            AgentName = input.AgentName,
            ToolName = input.ToolName,
            Arguments = input.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(input.Arguments),
            CallId = input.CallId,
            SessionId = ctx.Info.WorkflowId,
            StateBag = stateBag,
            RequiresApproval = input.RequiresApproval,
        };

        // Snapshot the bag's serialized form before the interceptor runs so we can detect
        // (and propagate) in-place mutations afterwards (X-2 StateBag write-back).
        var stateBagBefore = stateBag is { Count: > 0 } ? stateBag.Serialize().GetRawText() : null;

        DurableToolDecision decision;
        try
        {
            decision = await toolInterceptor
                .BeforeToolCallAsync(toolContext, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var captureTelemetryContent =
                TemporalCommunity.Extensions.AI.Internal.DurableTelemetryContentCapturePolicy.Resolve(
                    scope.ServiceProvider.GetService<TemporalCommunity.Extensions.AI.DurableExecutionOptions>());
            TemporalCommunity.Extensions.AI.Internal.DurableFailureLogger.LogException(
                _logger,
                ex,
                captureTelemetryContent,
                "BeforeToolCallAsync",
                $"agent {input.AgentName}, tool {input.ToolName}; defaulting to Block");
            return new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.Block,
                Message = $"Interceptor threw an exception: {ex.Message}",
            };
        }

        var result = DurableToolInterceptorResult.FromDecision(decision);

        // X-2: propagate StateBag mutations the interceptor made in place, including removal of
        // its final key. The workflow converts this snapshot to a key-level delta before dispatch.
        if (toolContext.StateBag is { } mutatedBag)
        {
            var stateBagAfter = mutatedBag.Serialize();
            if (!string.Equals(stateBagBefore, stateBagAfter.GetRawText(), StringComparison.Ordinal))
            {
                result = result.WithUpdatedStateBag(stateBagAfter);
            }
        }

        return result;
    }

    [Activity("TemporalCommunity.Extensions.Agents.InvokeAgentTool")]
    public async Task<InvokeAgentToolResult> InvokeAgentToolAsync(InvokeAgentToolInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CallId is { Length: > 256 })
        {
            throw new ApplicationFailureException(
                "Tool call IDs cannot exceed 256 characters.",
                errorType: "DurableToolCallIdTooLong",
                nonRetryable: true);
        }

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var blueprint = ResolveBlueprint(input.AgentName);

        if (!blueprint.ToolRegistrations.TryGetValue(input.ToolName, out var toolRegistration))
        {
            throw new InvalidOperationException(
                $"Tool '{input.ToolName}' is not registered on agent '{input.AgentName}'.");
        }

        if ((input.PreparedTool?.IsTemporalMemorySearch == true) != (toolRegistration.MemorySearch is not null))
        {
            throw BuildMemorySearchConfigurationFailure(
                $"Tool '{input.ToolName}' does not match the expected durable route on agent '{input.AgentName}'.");
        }

        ctx.Heartbeat();

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentToolInvokeSpanName,
            ActivityKind.Internal);
        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentToolNameAttribute, input.ToolName);
        if (!string.IsNullOrEmpty(input.CallId))
        {
            span?.SetTag(TemporalAgentTelemetry.AgentToolCallIdAttribute, input.CallId);
        }

        // Open a per-call scope so any scoped services the tool resolves via TemporalAgentContext
        // are properly lifetime-managed and not captured as captive singletons.
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var fn = ResolveToolFunction(toolRegistration, scope.ServiceProvider, input.AgentName);
        if (input.PreparedTool is { } preparedTool)
        {
            if (!string.Equals(preparedTool.Name, input.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApplicationFailureException(
                    $"Prepared provider-tool binding '{preparedTool.Name}' does not match dispatched tool '{input.ToolName}'.",
                    errorType: nameof(DurableConfigurationException),
                    nonRetryable: true);
            }

            if (preparedTool.IsFrozenProviderSelection)
            {
                if (preparedTool.Declaration is null)
                {
                    throw new ApplicationFailureException(
                        $"Provider-selected tool '{input.ToolName}' on agent '{input.AgentName}' has no frozen declaration.",
                        errorType: nameof(DurableConfigurationException),
                        nonRetryable: true);
                }

                preparedTool.Declaration.ValidateImplementation(fn);
            }
        }

        if (fn.GetService<ApprovalRequiredAIFunction>() is not null && !input.ApprovalAccepted)
        {
            throw new ApplicationFailureException(
                $"Tool '{input.ToolName}' requires a durable accepted approval before invocation.",
                errorType: "DurableToolApprovalRequired",
                nonRetryable: true);
        }

        // Set up TemporalAgentContext for the tool invocation so tools that call
        // TemporalAgentContext.Current (e.g. for RequestApprovalAsync) work in the
        // per-tool activity path. Mirrors the SetCurrent/clear-in-finally pattern at
        // RunDurableAgentStepAsync line ~296. Without this, HITL tools and any tool
        // that needs the agent context are broken when dispatched as InvokeAgentTool
        // activities (which is the default).
        //
        // SessionId parse is wrapped in try/catch: in tests, ActivityEnvironment uses
        // arbitrary workflow IDs (e.g. "test") that don't match the agent session
        // prefix. When parsing fails, we skip context setup — a tool that needs the
        // context will throw the same "No TemporalAgentContext is available" error
        // as before this fix, but tools that don't need it continue to work.
        var contextSetUp = false;
        // X-1: hold the session so we can both (a) seed the tool with the carried StateBag and
        // (b) capture the tool's StateBag mutations for write-back after invocation.
        TemporalAgentSession? session = null;
        try
        {
            var workflowId = input.SessionId ?? ctx.Info.WorkflowId;
            ArgumentNullException.ThrowIfNull(workflowId, nameof(ctx.Info.WorkflowId));
            var sessionId = TemporalAgentSessionId.Parse(workflowId);

            // Validate that the parsed agent name matches the tool's registered agent.
            // A mismatched ID would let tools call RequestApprovalAsync against the wrong
            // workflow; skip context setup in that case.
            if (!sessionId.AgentName.Equals(input.AgentName, StringComparison.OrdinalIgnoreCase))
            {
                // Workflow ID parsed but belongs to a different agent.
                // Leave TemporalAgentContext unset, recording why so a tool that reaches for it
                // gets told which path it is on rather than a bare "no context".
                TemporalAgentContext.SetUnavailable(
                    ContextUnavailableReason.AgentIdentityMismatch,
                    workflowId,
                    detail: $"names agent '{sessionId.AgentName}', not '{input.AgentName}'");
            }
            else
            {
                // X-1: build the session from the carried StateBag (was literal null before),
                // so tools and any AIContextProvider they consult see accumulated state.
                session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);
                var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, scope.ServiceProvider);
                TemporalAgentContext.SetCurrent(temporalContext);
                contextSetUp = true;
            }
        }
        catch (FormatException)
        {
            // Workflow ID isn't an agent session ID, so this activity cannot attach a managed
            // agent session. This can occur in standalone activity tests or non-session dispatch.
            TemporalAgentContext.SetUnavailable(
                ContextUnavailableReason.NoAgentSession, input.SessionId ?? ctx.Info.WorkflowId);
        }

        // Snapshot the bag's serialized form before invocation so we can detect (and write back)
        // tool-driven mutations afterwards (X-1). Only computed on the real agent-session path.
        var stateBagBefore = session?.SerializeStateBag();

        try
        {
            if (input.PreparedTool?.IsTemporalMemorySearch == true)
            {
                if (session is null || input.PreparedTool is not { } memoryPreparedTool
                    || toolRegistration.MemorySearch is not { } memorySearch)
                {
                    throw BuildMemorySearchConfigurationFailure(
                        $"Temporal memory-search tool '{input.ToolName}' requires a managed agent session.");
                }

                var isolatedSession = TemporalAgentSession.FromStateBag(
                    session.SessionId,
                    input.SerializedStateBag);
                var chatClient = blueprint.Registration.ChatClient(scope.ServiceProvider);
                using var pipeline = BuildLiveAgentPipeline(
                    blueprint,
                    chatClient,
                    isolatedSession,
                    new TemporalAgentTurnTelemetryContext(null),
                    scope.ServiceProvider);
                fn = await ResolveTemporalMemorySearchFunctionAsync(
                    memorySearch,
                    memoryPreparedTool,
                    session.SessionId,
                    input.SerializedStateBag,
                    pipeline.Agent,
                    scope.ServiceProvider,
                    ct).ConfigureAwait(false);
            }

            _logger.LogAgentToolInvocationStarted(input.AgentName, input.ToolName);

            var arguments = input.Arguments is null
                ? new AIFunctionArguments()
                : new AIFunctionArguments(input.Arguments);
            arguments.Services = scope.ServiceProvider;

            object? result;
            var failed = false;
            var terminate = false;
            try
            {
                if (blueprint.Registration.ToolMiddleware is not null)
                {
                    (result, terminate) = await TemporalCommunity.Extensions.AI.Internal.DurableActivityHeartbeat
                        .RunWithHeartbeatsAsync(ctx, async () =>
                            await DurableToolMiddleware.InvokeAsync(
                                blueprint.Registration, input, fn, arguments, ct).ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
                else
                {
                    result = await TemporalCommunity.Extensions.AI.Internal.DurableActivityHeartbeat
                        .RunWithHeartbeatsAsync(
                            ctx,
                            async () => await fn.InvokeAsync(arguments, ct).ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
                _logger.LogAgentToolInvocationCompleted(input.AgentName, input.ToolName);
            }
            catch (DurableToolFailureException exception) when (!ct.IsCancellationRequested)
            {
                failed = true;
                span?.SetStatus(ActivityStatusCode.Error, "Tool reported a safe failure.");
                var captureTelemetryContent =
                    TemporalCommunity.Extensions.AI.Internal.DurableTelemetryContentCapturePolicy.Resolve(
                        scope.ServiceProvider.GetService<TemporalCommunity.Extensions.AI.DurableExecutionOptions>());
                TemporalCommunity.Extensions.AI.Internal.DurableFailureLogger.LogException(
                    _logger,
                    exception,
                    captureTelemetryContent,
                    "InvokeAgentTool",
                    $"agent {input.AgentName}, tool {input.ToolName}; classified safe failure");
                result = $"[Tool failed safely] {exception.ModelVisibleMessage}";
            }

            var stateBagDelta = session is null
                ? null
                : StateBagMerge.CreateDelta(stateBagBefore, session.SerializeStateBag());

            var toolResult = new InvokeAgentToolResult
            {
                Result = result,
                CallId = input.CallId,
                Failed = failed,
                Terminate = terminate,
                StateBagDelta = stateBagDelta,
            };
            try
            {
                TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                    ctx.PayloadConverter,
                    toolResult,
                    input.MaximumTransportPayloadSizeBytes,
                    input.PayloadCodecExpansionAllowanceBytes,
                    "tool activity result");
            }
            catch (TemporalCommunity.Extensions.AI.Exceptions.DurableTransportPayloadTooLargeException)
            {
                toolResult = new InvokeAgentToolResult
                {
                    Result = InvokeAgentToolResult.ResultTooLargeMessage,
                    CallId = input.CallId,
                    Failed = failed,
                    Terminate = terminate,
                    StateBagDelta = stateBagDelta,
                };
                try
                {
                    TemporalCommunity.Extensions.AI.Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                        ctx.PayloadConverter,
                        toolResult,
                        input.MaximumTransportPayloadSizeBytes,
                        input.PayloadCodecExpansionAllowanceBytes,
                        "bounded tool activity result");
                }
                catch (Exception exception)
                {
                    throw TemporalCommunity.Extensions.AI.Internal.DurableActivityFailurePolicy.CreateBounded(
                        exception,
                        "DurableToolResultTooLarge",
                        nonRetryable: true);
                }

            }
            catch (ApplicationFailureException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw TemporalCommunity.Extensions.AI.Internal.DurableActivityFailurePolicy.CreateBounded(
                    exception,
                    "DurableToolResultSerializationFailed",
                    nonRetryable: true);
            }

            return toolResult;
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var captureTelemetryContent =
                TemporalCommunity.Extensions.AI.Internal.DurableTelemetryContentCapturePolicy.Resolve(
                    scope.ServiceProvider.GetService<TemporalCommunity.Extensions.AI.DurableExecutionOptions>());
            TemporalCommunity.Extensions.AI.Internal.DurableFailureLogger.LogException(
                _logger,
                ex,
                captureTelemetryContent,
                "InvokeAgentTool",
                $"agent {input.AgentName}, tool {input.ToolName}",
                "DurableAgentToolFailed");
            throw;
        }
        finally
        {
            if (contextSetUp)
            {
                TemporalAgentContext.SetCurrent(null);
            }
            else
            {
                // Drop the recorded reason so it cannot outlive this invocation and mislabel an
                // unrelated one.
                TemporalAgentContext.SetUnavailable(null);
            }
        }
    }

    private static AIFunction ResolveToolFunction(
        DurableToolRegistration registration,
        IServiceProvider scopedServices,
        string agentName)
    {
        var function = registration.Factory(scopedServices)
            ?? throw CreateToolFactoryFailure(
                $"Tool factory for '{registration.Name}' on agent '{agentName}' returned null.");

        if (!string.Equals(function.Name, registration.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateToolFactoryFailure(
                $"Tool factory for '{registration.Name}' on agent '{agentName}' returned an AIFunction " +
                $"named '{function.Name}'. The resolved name must match the registration name.");
        }

        return function;
    }

    private static ApplicationFailureException CreateToolFactoryFailure(string message)
    {
        var exception = new DurableConfigurationException(message);
        return new ApplicationFailureException(
            exception.Message,
            exception,
            errorType: nameof(DurableConfigurationException),
            nonRetryable: true);
    }
}
