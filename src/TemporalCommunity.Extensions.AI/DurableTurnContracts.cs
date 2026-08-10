using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Describes one application-owned turn executed by a
/// <see cref="DurableToolWorkflowBase{TRequestData, TTurnState}"/>.
/// </summary>
public sealed class DurableTurnRequest<TRequestData, TTurnState>
{
    /// <summary>Gets the messages supplied to the model for this turn.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Gets immutable application data available to invocation-scoped tool factories for this
    /// turn. The library does not add this data to model messages, arguments, or schemas.
    /// </summary>
    public required TRequestData RequestData { get; init; }

    /// <summary>Gets the application-owned state at the start of this turn.</summary>
    public TTurnState? InitialTurnState { get; init; }

    /// <summary>Gets optional observability correlation metadata.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Gets the optional application conversation identifier. The Temporal workflow ID is used
    /// when this value is absent.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>Gets model options for this turn.</summary>
    public ChatOptions? ChatOptions { get; init; }

    /// <summary>Gets durable turn behavior.</summary>
    public DurableTurnOptions Options { get; init; } = new();
}

/// <summary>Contains the completed model response and final application-owned turn state.</summary>
public sealed class DurableTurnResult<TTurnState>
{
    /// <summary>Gets the complete model/tool response for the turn.</summary>
    public required ChatResponse Response { get; init; }

    /// <summary>Gets why the managed model/tool loop returned.</summary>
    public required DurableTurnCompletionReason CompletionReason { get; init; }

    /// <summary>Gets the last successfully recorded state for this turn.</summary>
    public TTurnState? FinalTurnState { get; init; }
}

/// <summary>Configures package-owned durable tool dispatch for one turn.</summary>
public sealed class DurableTurnOptions
{
    /// <summary>
    /// Gets the dispatch mode. Sequential dispatch is the default so state changes have one
    /// deterministic order.
    /// </summary>
    public DurableToolDispatchMode DispatchMode { get; init; } = DurableToolDispatchMode.Sequential;
}

/// <summary>Specifies how tool calls from one model response are scheduled.</summary>
[JsonConverter(typeof(JsonNumberEnumConverter<DurableToolDispatchMode>))]
public enum DurableToolDispatchMode
{
    /// <summary>Run approved tools one at a time in original model-call order.</summary>
    Sequential = 0,

    /// <summary>Run approved tools concurrently. Turn-state replacement is not permitted.</summary>
    Parallel = 1,
}

/// <summary>Specifies why a durable turn produced a result.</summary>
[JsonConverter(typeof(JsonNumberEnumConverter<DurableTurnCompletionReason>))]
public enum DurableTurnCompletionReason
{
    /// <summary>The model produced a final response.</summary>
    FinalResponse = 0,

    /// <summary>The configured model/tool iteration limit was exhausted.</summary>
    IterationLimitReached = 1,
}
