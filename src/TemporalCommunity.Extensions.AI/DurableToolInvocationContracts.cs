using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Activity-local application data, turn state, dispatch behavior, and framework metadata supplied
/// to an invocation-scoped durable tool factory.
/// </summary>
public sealed class DurableToolInvocationContext<TRequestData, TTurnState>
{
    internal DurableToolInvocationContext(
        TRequestData requestData,
        TTurnState? turnState,
        DurableToolDispatchMode dispatchMode,
        DurableToolInvocationMetadata metadata)
    {
        RequestData = requestData;
        TurnState = turnState;
        DispatchMode = dispatchMode;
        Metadata = metadata;
    }

    /// <summary>Gets the immutable application data supplied for this turn.</summary>
    public TRequestData RequestData { get; }

    /// <summary>Gets the last successfully recorded state for this turn.</summary>
    public TTurnState? TurnState { get; }

    /// <summary>Gets the dispatch mode selected for this turn.</summary>
    public DurableToolDispatchMode DispatchMode { get; }

    /// <summary>Gets framework-created metadata for this activity attempt.</summary>
    public DurableToolInvocationMetadata Metadata { get; }
}

/// <summary>Framework-created identity and diagnostic metadata for one tool activity attempt.</summary>
public sealed class DurableToolInvocationMetadata
{
    internal DurableToolInvocationMetadata(
        string @namespace,
        string workflowId,
        string workflowRunId,
        string activityId,
        int attempt,
        string taskQueue,
        string toolName,
        string? toolCallId,
        int modelIteration,
        int callIndex,
        string? conversationId,
        string? correlationId,
        string idempotencyKey)
    {
        Namespace = @namespace;
        WorkflowId = workflowId;
        WorkflowRunId = workflowRunId;
        ActivityId = activityId;
        Attempt = attempt;
        TaskQueue = taskQueue;
        ToolName = toolName;
        ToolCallId = toolCallId;
        ModelIteration = modelIteration;
        CallIndex = callIndex;
        ConversationId = conversationId;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
    }

    public string Namespace { get; }
    public string WorkflowId { get; }
    public string WorkflowRunId { get; }
    public string ActivityId { get; }
    public int Attempt { get; }
    public string TaskQueue { get; }
    public string ToolName { get; }
    public string? ToolCallId { get; }
    public int ModelIteration { get; }
    public int CallIndex { get; }
    public string? ConversationId { get; }
    public string? CorrelationId { get; }
    public string IdempotencyKey { get; }
}

/// <summary>
/// Pairs the ordinary MEAI function invoked by a tool activity with an optional post-success turn-
/// state completion operation.
/// </summary>
public sealed class DurableToolActivation<TTurnState>
{
    /// <summary>Gets the ordinary function, including any MEAI decorators, to invoke.</summary>
    public required AIFunction Function { get; init; }

    /// <summary>
    /// Gets an optional operation that observes the successfully marshalled MEAI result and either
    /// leaves state unchanged or supplies a complete replacement. It must be side-effect free.
    /// </summary>
    public Func<object?, CancellationToken, ValueTask<DurableStateUpdate<TTurnState>>>? CompleteState
    {
        get;
        init;
    }
}

/// <summary>Represents either no turn-state change or an explicit complete replacement.</summary>
#pragma warning disable CA1000 // Generic factory members keep the state type inferred at the call site.
public readonly struct DurableStateUpdate<TTurnState>
{
    private DurableStateUpdate(bool hasReplacement, TTurnState? value)
    {
        HasReplacement = hasReplacement;
        Value = value;
    }

    /// <summary>Gets whether this value supplies a replacement, including a replacement with null.</summary>
    public bool HasReplacement { get; }

    /// <summary>Gets the replacement value when <see cref="HasReplacement"/> is true.</summary>
    public TTurnState? Value { get; }

    /// <summary>Gets a value that leaves the current turn state unchanged.</summary>
    public static DurableStateUpdate<TTurnState> Unchanged => default;

    /// <summary>Creates an explicit complete replacement. A null value is still a replacement.</summary>
    public static DurableStateUpdate<TTurnState> Replace(TTurnState? value) => new(true, value);
}
#pragma warning restore CA1000
