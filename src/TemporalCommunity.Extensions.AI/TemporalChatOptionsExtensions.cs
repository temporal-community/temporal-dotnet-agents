using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Extension methods for setting Temporal-specific metadata on <see cref="ChatOptions"/>
/// via <see cref="ChatOptions.AdditionalProperties"/>.
/// </summary>
/// <remarks>
/// All <c>With*</c> methods on this class mutate <see cref="ChatOptions.AdditionalProperties"/>
/// on the provided instance in-place and return the same instance for call chaining. If you
/// need to preserve the original options unchanged, clone it first:
/// <code>
/// var opts = new ChatOptions(existingOptions).WithActivityTimeout(TimeSpan.FromMinutes(10));
/// </code>
/// </remarks>
public static class TemporalChatOptionsExtensions
{
    /// <summary>Key for per-request activity timeout override.</summary>
    public const string ActivityTimeoutKey = "temporal.activity.timeout";

    /// <summary>Key for per-request maximum retry attempts override.</summary>
    public const string MaxRetryAttemptsKey = "temporal.retry.max_attempts";

    /// <summary>Key for per-request heartbeat timeout override.</summary>
    public const string HeartbeatTimeoutKey = "temporal.heartbeat.timeout";

    /// <summary>Key for per-request keyed DI service key for <see cref="IChatClient"/> resolution.</summary>
    public const string ChatClientKeySettingKey = "temporal.chatClientKey";

    /// <summary>
    /// Key prefix for per-request tags applied to the durable model-activity span.
    /// User code calls <see cref="WithChatClientTag(ChatOptions, string, string)"/>, which writes
    /// entries under <c>temporal.chatClientTag.{name}</c> on
    /// <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    /// <remarks>
    /// The prefix lets the provider-boundary filter remove all activity-only tag entries with a
    /// single check before invoking the configured <see cref="IChatClient"/>.
    /// </remarks>
    public const string ChatClientTagsKeyPrefix = "temporal.chatClientTag.";

    /// <summary>
    /// Sets a per-request activity timeout that overrides <see cref="DurableExecutionOptions.ActivityTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Mutates <see cref="ChatOptions.AdditionalProperties"/> on <paramref name="options"/> in-place
    /// and returns the same instance. Clone first if the original must be preserved.
    /// </remarks>
    public static ChatOptions WithActivityTimeout(this ChatOptions options, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[ActivityTimeoutKey] = timeout.ToString("c");
        return options;
    }

    /// <summary>
    /// Sets a per-request maximum retry attempts that overrides the default retry policy.
    /// </summary>
    /// <remarks>
    /// Mutates <see cref="ChatOptions.AdditionalProperties"/> on <paramref name="options"/> in-place
    /// and returns the same instance. Clone first if the original must be preserved.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxAttempts"/> is zero or negative.
    /// </exception>
    public static ChatOptions WithMaxRetryAttempts(this ChatOptions options, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateMaxRetryAttempts(maxAttempts, nameof(maxAttempts));
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[MaxRetryAttemptsKey] = maxAttempts.ToString();
        return options;
    }

    /// <summary>
    /// Sets a per-request heartbeat timeout that overrides <see cref="DurableExecutionOptions.HeartbeatTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Mutates <see cref="ChatOptions.AdditionalProperties"/> on <paramref name="options"/> in-place
    /// and returns the same instance. Clone first if the original must be preserved.
    /// </remarks>
    public static ChatOptions WithHeartbeatTimeout(this ChatOptions options, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[HeartbeatTimeoutKey] = timeout.ToString("c");
        return options;
    }

    /// <summary>
    /// Sets the keyed DI service key used to resolve <see cref="IChatClient"/> for this request.
    /// Takes precedence over <see cref="DurableExecutionOptions.DefaultChatClientKey"/>.
    /// Overriding back to the unkeyed client is not supported; omit this call to use the worker default.
    /// </summary>
    /// <remarks>
    /// Mutates <see cref="ChatOptions.AdditionalProperties"/> on <paramref name="options"/> in-place
    /// and returns the same instance. Clone first if the original must be preserved.
    /// </remarks>
    public static ChatOptions WithChatClientKey(this ChatOptions options, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key, nameof(key));
        (options.AdditionalProperties ??= new())[ChatClientKeySettingKey] = key;
        return options;
    }

    /// <summary>
    /// Sets a per-request tag pair applied to the durable model-activity span. Multiple calls add
    /// multiple tags; the latest value for a given <paramref name="name"/> wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The durable chat activity reads these entries from
    /// <see cref="ChatOptions.AdditionalProperties"/> and attaches them to
    /// <c>Activity.Current</c> immediately before provider invocation. No client wrapper or keyed
    /// registration is required.
    /// </para>
    /// <para>
    /// Tags cross the durable boundary to the worker activity. The library removes every
    /// Temporal-owned tag key before the ultimate provider call.
    /// </para>
    /// <para>
    /// Mutates <see cref="ChatOptions.AdditionalProperties"/> on <paramref name="options"/>
    /// in-place and returns the same instance.
    /// </para>
    /// </remarks>
    public static ChatOptions WithChatClientTag(this ChatOptions options, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
        ArgumentNullException.ThrowIfNull(value);
        (options.AdditionalProperties ??= new())[ChatClientTagsKeyPrefix + name] = value;
        return options;
    }

    /// <summary>
    /// Tries to read a per-request activity timeout from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static TimeSpan? GetActivityTimeout(this ChatOptions? options) =>
        GetTimeSpanProperty(options, ActivityTimeoutKey);

    /// <summary>
    /// Tries to read a per-request heartbeat timeout from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static TimeSpan? GetHeartbeatTimeout(this ChatOptions? options) =>
        GetTimeSpanProperty(options, HeartbeatTimeoutKey);

    /// <summary>
    /// Tries to read a per-request max retry attempts from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static int? GetMaxRetryAttempts(this ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue(MaxRetryAttemptsKey, out var value) != true)
            return null;

        if (TryGetString(value) is { } s && int.TryParse(s, out var v))
            return ValidateMaxRetryAttempts(v, MaxRetryAttemptsKey);
        if (value is int direct) // backward compat
            return ValidateMaxRetryAttempts(direct, MaxRetryAttemptsKey);
        if (value is JsonElement { ValueKind: JsonValueKind.Number } element &&
            element.TryGetInt32(out var jsonValue))
            return ValidateMaxRetryAttempts(jsonValue, MaxRetryAttemptsKey);

        return null;
    }

    /// <summary>
    /// Tries to read a per-request chat client key from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static string? GetChatClientKey(this ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(ChatClientKeySettingKey, out var value) == true
            ? TryGetString(value)
            : null;

    /// <summary>
    /// Collects all per-request tag entries (keys prefixed with
    /// <see cref="ChatClientTagsKeyPrefix"/>) from <see cref="ChatOptions.AdditionalProperties"/>.
    /// Returns an empty collection (not <see langword="null"/>) when no tags are set so consumers
    /// can iterate without null-checks.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> GetChatClientTags(this ChatOptions? options)
    {
        if (options?.AdditionalProperties is null)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        var tags = new List<KeyValuePair<string, string>>();
        foreach (var kvp in options.AdditionalProperties)
        {
            if (kvp.Key.StartsWith(ChatClientTagsKeyPrefix, StringComparison.Ordinal)
                && TryGetString(kvp.Value) is { } value)
            {
                var name = kvp.Key.Substring(ChatClientTagsKeyPrefix.Length);
                tags.Add(new KeyValuePair<string, string>(name, value));
            }
        }
        return tags;
    }

    private static TimeSpan? GetTimeSpanProperty(ChatOptions? options, string key)
    {
        if (options?.AdditionalProperties?.TryGetValue(key, out var value) != true)
            return null;

        if (TryGetString(value) is { } s && TimeSpan.TryParseExact(s, "c", null, out var ts))
            return ts;
        if (value is TimeSpan direct) // backward compat
            return direct;

        return null;
    }

    private static string? TryGetString(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };

    private static int ValidateMaxRetryAttempts(int maxAttempts, string parameterName)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                maxAttempts,
                "Temporal retry maximum attempts must be greater than zero.");
        }

        return maxAttempts;
    }
}
