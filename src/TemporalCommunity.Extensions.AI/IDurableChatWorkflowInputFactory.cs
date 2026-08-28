using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Creates the canonical, replay-frozen start input for a managed durable chat workflow.
/// </summary>
/// <remarks>
/// Resolve this service outside workflow code. For worker-owned toolsets it creates a thin input;
/// the workflow resolves declarations and durable policy once on the worker. For the advanced
/// caller-owned declaration mode it snapshots registered declarations and policy. Custom
/// workflows should start from this factory rather than reconstructing session configuration.
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
    private readonly Internal.DurableFunctionDeclarationRegistry? _declarationRegistry;
    private readonly bool _useWorkerToolsets;
    private readonly Lazy<IReadOnlyDictionary<string, ActivityOptions>?> _toolActivityOptions;
    private readonly Lazy<ActivityOptions?> _interceptorActivityOptions;
    private readonly Lazy<IReadOnlyDictionary<string, ActivityOptions>?> _interceptorToolActivityOptions;
    private readonly Lazy<IReadOnlyList<string>?> _interceptorSkippedTools;
    private readonly Lazy<IReadOnlyList<string>?> _requiresApprovalTools;
    private readonly Lazy<IReadOnlyDictionary<string, TimeSpan>?> _toolApprovalTimeouts;

    internal DurableChatWorkflowInputFactory(
        DurableExecutionOptions options,
        DurableFunctionRegistry? functionRegistry,
        DurableChatToolOptionsRegistry? toolOptionsRegistry,
        Internal.DurableFunctionDeclarationRegistry? declarationRegistry = null,
        IEnumerable<DurableToolsetRegistration>? toolsetRegistrations = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _functionRegistry = functionRegistry;
        _toolOptionsRegistry = toolOptionsRegistry;
        _declarationRegistry = declarationRegistry;
        _useWorkerToolsets = toolsetRegistrations?.Any() == true;
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
        RetryPolicy = _options.RetryPolicy,
        ApprovalTimeout = _options.ApprovalTimeout,
        EnableSearchAttributes = _options.EnableSearchAttributes,
        MaxEntryCount = _options.MaxEntryCount,
        HistoryReducerKey = _options.DefaultHistoryReducerKey,
        ToolActivityOptions = _useWorkerToolsets ? null : _toolActivityOptions.Value,
        MaxToolCallsPerTurn = _options.MaxToolCallsPerTurn,
        MaximumConsecutiveErrorsPerRequest = _options.MaximumConsecutiveErrorsPerRequest,
        IncludeDetailedErrors = _options.IncludeDetailedErrors,
        InterceptorActivityOptions = _useWorkerToolsets ? null : _interceptorActivityOptions.Value,
        InterceptorToolActivityOptions = _useWorkerToolsets ? null : _interceptorToolActivityOptions.Value,
        InterceptorSkippedTools = _useWorkerToolsets ? null : _interceptorSkippedTools.Value,
        RequiresApprovalTools = _useWorkerToolsets ? null : _requiresApprovalTools.Value,
        ToolApprovalTimeouts = _useWorkerToolsets ? null : _toolApprovalTimeouts.Value,
        ToolDeclarations = !_useWorkerToolsets && _declarationRegistry is { Count: > 0 }
            ? _declarationRegistry.Values.ToList()
            : null,
    };

    private Temporalio.Common.RetryPolicy EffectiveToolRetryPolicy =>
        Internal.DefaultRetryPolicy.ResolveForTool(_options.RetryPolicy);

    private IReadOnlyDictionary<string, ActivityOptions>? BuildToolActivityOptions()
    {
        var toolNames = _declarationRegistry?.Keys
            ?? (IEnumerable<string>?)_functionRegistry?.Keys;
        if (toolNames is null || !toolNames.Any())
        {
            return null;
        }

        var result = new Dictionary<string, ActivityOptions>(
            _declarationRegistry?.Count ?? _functionRegistry?.Count ?? 0,
            StringComparer.Ordinal);

        foreach (var toolName in toolNames)
        {
            DurableChatToolOptions? perTool = null;
            _toolOptionsRegistry?.TryGetValue(toolName, out perTool);

            result[toolName] = new ActivityOptions
            {
                StartToCloseTimeout = perTool?.StartToCloseTimeout ?? _options.ActivityTimeout,
                HeartbeatTimeout = perTool?.HeartbeatTimeout ?? _options.HeartbeatTimeout,
                RetryPolicy = perTool?.RetryPolicy ?? EffectiveToolRetryPolicy,
                Summary = toolName,
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
            RetryPolicy = EffectiveToolRetryPolicy,
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
                result ??= new Dictionary<string, ActivityOptions>(StringComparer.Ordinal);
                result[kvp.Key] = new ActivityOptions
                {
                    StartToCloseTimeout = kvp.Value.InterceptorTimeout,
                    HeartbeatTimeout = _options.HeartbeatTimeout,
                    RetryPolicy = EffectiveToolRetryPolicy,
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
                result ??= new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
                result[kvp.Key] = timeout;
            }
        }

        return result;
    }
}
