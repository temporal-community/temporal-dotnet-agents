using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Creates the canonical, replay-frozen start input for a managed durable chat workflow.
/// </summary>
/// <remarks>
/// Resolve this service outside workflow code. It snapshots worker defaults plus registered
/// durable-tool, interceptor, retry, timeout, and approval settings into serializable workflow
/// input. Custom workflows that reuse the managed tool loop should start from this factory rather
/// than reconstructing configuration independently.
/// </remarks>
public interface IDurableChatWorkflowInputFactory
{
    /// <summary>
    /// Creates a workflow input containing the current host's canonical frozen configuration.
    /// </summary>
    /// <returns>A new input record. Frozen collection values may be shared between calls.</returns>
    DurableChatWorkflowInput Create();
}

internal sealed class DurableChatWorkflowInputFactory : IDurableChatWorkflowInputFactory
{
    private readonly DurableExecutionOptions _options;
    private readonly DurableFunctionRegistry? _functionRegistry;
    private readonly DurableChatToolOptionsRegistry? _toolOptionsRegistry;
    private readonly Lazy<IReadOnlyDictionary<string, ActivityOptions>?> _toolActivityOptions;
    private readonly Lazy<ActivityOptions?> _interceptorActivityOptions;
    private readonly Lazy<IReadOnlyDictionary<string, ActivityOptions>?> _interceptorToolActivityOptions;
    private readonly Lazy<IReadOnlyList<string>?> _interceptorSkippedTools;
    private readonly Lazy<IReadOnlyList<string>?> _requiresApprovalTools;
    private readonly Lazy<IReadOnlyDictionary<string, TimeSpan>?> _toolApprovalTimeouts;

    internal DurableChatWorkflowInputFactory(
        DurableExecutionOptions options,
        DurableFunctionRegistry? functionRegistry,
        DurableChatToolOptionsRegistry? toolOptionsRegistry)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _functionRegistry = functionRegistry;
        _toolOptionsRegistry = toolOptionsRegistry;
        _toolActivityOptions = new(BuildToolActivityOptions, LazyThreadSafetyMode.ExecutionAndPublication);
        _interceptorActivityOptions = new(BuildInterceptorActivityOptions, LazyThreadSafetyMode.ExecutionAndPublication);
        _interceptorToolActivityOptions = new(
            BuildInterceptorToolActivityOptions,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _interceptorSkippedTools = new(
            BuildInterceptorSkippedTools,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _requiresApprovalTools = new(
            BuildRequiresApprovalTools,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _toolApprovalTimeouts = new(
            BuildToolApprovalTimeouts,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public DurableChatWorkflowInput Create() => new()
    {
        TimeToLive = _options.SessionTimeToLive,
        ActivityTimeout = _options.ActivityTimeout,
        HeartbeatTimeout = _options.HeartbeatTimeout,
        RetryPolicy = EffectiveRetryPolicy,
        ApprovalTimeout = _options.ApprovalTimeout,
        EnableSearchAttributes = _options.EnableSearchAttributes,
        MaxEntryCount = _options.MaxEntryCount,
        HistoryReducer = _options.HistoryReducer,
        HistoryReducerKey = _options.DefaultHistoryReducerKey,
        ToolActivityOptions = _toolActivityOptions.Value,
        MaxToolCallsPerTurn = _options.MaxToolCallsPerTurn,
        MaximumConsecutiveErrorsPerRequest = _options.MaximumConsecutiveErrorsPerRequest,
        IncludeDetailedErrors = _options.IncludeDetailedErrors,
        InterceptorActivityOptions = _interceptorActivityOptions.Value,
        InterceptorToolActivityOptions = _interceptorToolActivityOptions.Value,
        InterceptorSkippedTools = _interceptorSkippedTools.Value,
        RequiresApprovalTools = _requiresApprovalTools.Value,
        ToolApprovalTimeouts = _toolApprovalTimeouts.Value,
    };

    private Temporalio.Common.RetryPolicy EffectiveRetryPolicy =>
        Internal.DefaultRetryPolicy.Resolve(_options.RetryPolicy);

    private IReadOnlyDictionary<string, ActivityOptions>? BuildToolActivityOptions()
    {
        if (_functionRegistry is null || _functionRegistry.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, ActivityOptions>(
            _functionRegistry.Count,
            StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _functionRegistry)
        {
            DurableChatToolOptions? perTool = null;
            _toolOptionsRegistry?.TryGetValue(kvp.Key, out perTool);

            result[kvp.Key] = new ActivityOptions
            {
                StartToCloseTimeout = perTool?.StartToCloseTimeout ?? _options.ActivityTimeout,
                HeartbeatTimeout = perTool?.HeartbeatTimeout ?? _options.HeartbeatTimeout,
                RetryPolicy = perTool?.RetryPolicy ?? EffectiveRetryPolicy,
                Summary = kvp.Key,
            };
        }

        return result;
    }

    private ActivityOptions? BuildInterceptorActivityOptions()
    {
        if (_options.DefaultToolInterceptor is null)
        {
            return null;
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = _options.ActivityTimeout,
            HeartbeatTimeout = _options.HeartbeatTimeout,
            RetryPolicy = EffectiveRetryPolicy,
        };
    }

    private IReadOnlyDictionary<string, ActivityOptions>? BuildInterceptorToolActivityOptions()
    {
        if (_options.DefaultToolInterceptor is null || _toolOptionsRegistry is null)
        {
            return null;
        }

        Dictionary<string, ActivityOptions>? result = null;
        foreach (var kvp in _toolOptionsRegistry)
        {
            if (kvp.Value.InterceptorTimeout.HasValue)
            {
                result ??= new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);
                result[kvp.Key] = new ActivityOptions
                {
                    StartToCloseTimeout = kvp.Value.InterceptorTimeout,
                    HeartbeatTimeout = _options.HeartbeatTimeout,
                    RetryPolicy = EffectiveRetryPolicy,
                };
            }
        }

        return result;
    }

    private IReadOnlyList<string>? BuildInterceptorSkippedTools()
    {
        if (_options.DefaultToolInterceptor is null || _toolOptionsRegistry is null)
        {
            return null;
        }

        List<string>? result = null;
        foreach (var kvp in _toolOptionsRegistry)
        {
            if (kvp.Value.SkipInterceptorFlag)
            {
                (result ??= []).Add(kvp.Key);
            }
        }

        return result;
    }

    private IReadOnlyList<string>? BuildRequiresApprovalTools()
    {
        if (_toolOptionsRegistry is null)
        {
            return null;
        }

        List<string>? result = null;
        foreach (var kvp in _toolOptionsRegistry)
        {
            if (kvp.Value.RequireApprovalFlag)
            {
                (result ??= []).Add(kvp.Key);
            }
        }

        return result;
    }

    private IReadOnlyDictionary<string, TimeSpan>? BuildToolApprovalTimeouts()
    {
        if (_toolOptionsRegistry is null)
        {
            return null;
        }

        Dictionary<string, TimeSpan>? result = null;
        foreach (var kvp in _toolOptionsRegistry)
        {
            if (kvp.Value.ApprovalTimeout is { } timeout)
            {
                result ??= new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
                result[kvp.Key] = timeout;
            }
        }

        return result;
    }
}
