using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

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
    /// Key for per-request <see cref="IChatClientDecorator"/> resolution key. Decorators are
    /// resolved from keyed DI inside the durable-chat activity and applied around the resolved
    /// <see cref="IChatClient"/> before <c>GetStreamingResponseAsync</c> is called.
    /// </summary>
    public const string ChatClientFactoryKeySettingKey = "temporal.chatClientFactoryKey";

    /// <summary>
    /// Key prefix for per-request tag pairs consumed by the built-in <c>"tags"</c> decorator.
    /// User code calls <see cref="WithChatClientTag(ChatOptions, string, string)"/>, which writes
    /// entries under <c>temporal.chatClientTag.{name}</c> on
    /// <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    /// <remarks>
    /// Per the Q-ChatClientFactory-shape decision, the tag data path is intentionally scoped
    /// to feed the pre-registered <c>"tags"</c> decorator only — not a general-purpose data
    /// surface. The prefix lets the strip-list filter all tag entries with a single check
    /// at the workflow boundary.
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
    public static ChatOptions WithMaxRetryAttempts(this ChatOptions options, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(options);
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
    /// Sets the per-request <see cref="IChatClientDecorator"/> key. The named decorator is
    /// resolved from keyed DI inside the durable-chat activity and applied around the resolved
    /// <see cref="IChatClient"/> before the LLM call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes precedence over <see cref="DurableExecutionOptions.DefaultChatClientFactoryKey"/>.
    /// Pass an empty string to opt out of decoration entirely for this request (overrides the
    /// worker default).
    /// </para>
    /// <para>
    /// Mutates <see cref="ChatOptions.AdditionalProperties"/> on <paramref name="options"/>
    /// in-place and returns the same instance. Clone first if the original must be preserved.
    /// </para>
    /// <para>
    /// If <paramref name="key"/> names a decorator that is not registered in DI, the activity
    /// throws <see cref="Exceptions.DurableChatClientFactoryNotFoundException"/> at dispatch
    /// time. Built-in keys (e.g. <c>"tags"</c>) are pre-registered by <c>AddDurableAI</c> /
    /// <c>AddTemporalAgents</c>.
    /// </para>
    /// </remarks>
    public static ChatOptions WithChatClientFactoryKey(this ChatOptions options, string key)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(key);
        (options.AdditionalProperties ??= new())[ChatClientFactoryKeySettingKey] = key;
        return options;
    }

    /// <summary>
    /// Sets a per-request tag pair consumed by the built-in <c>"tags"</c> decorator. Multiple
    /// calls add multiple tags; the latest value for a given <paramref name="name"/> wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The built-in <c>"tags"</c> decorator (pre-registered by <c>AddDurableAI</c> /
    /// <c>AddTemporalAgents</c>) reads these entries from
    /// <see cref="ChatOptions.AdditionalProperties"/> and attaches them as tags to
    /// <c>Activity.Current</c> at dispatch time. Combined with
    /// <see cref="WithChatClientFactoryKey(ChatOptions, string)"/> set to <c>"tags"</c>, this
    /// covers per-tenant tagging, correlation IDs, and similar per-request OTel context without
    /// requiring a custom <see cref="IChatClientDecorator"/> registration.
    /// </para>
    /// <para>
    /// <b>Contract scope:</b> this data path is intentionally scoped to feed the pre-registered
    /// <c>"tags"</c> decorator only — not a general-purpose per-call data surface. Custom
    /// decorators should read their own keys from <see cref="ChatOptions.AdditionalProperties"/>
    /// using their own well-known prefix.
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

        if (value is string s && int.TryParse(s, out var v))
            return v;
        if (value is int direct) // backward compat
            return direct;

        return null;
    }

    /// <summary>
    /// Tries to read a per-request chat client key from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static string? GetChatClientKey(this ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(ChatClientKeySettingKey, out var v) == true
            ? v as string
            : null;

    /// <summary>
    /// Tries to read a per-request chat client factory key from
    /// <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static string? GetChatClientFactoryKey(this ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(ChatClientFactoryKeySettingKey, out var v) == true
            ? v as string
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
                && kvp.Value is string s)
            {
                var name = kvp.Key.Substring(ChatClientTagsKeyPrefix.Length);
                tags.Add(new KeyValuePair<string, string>(name, s));
            }
        }
        return tags;
    }

    private static TimeSpan? GetTimeSpanProperty(ChatOptions? options, string key)
    {
        if (options?.AdditionalProperties?.TryGetValue(key, out var value) != true)
            return null;

        if (value is string s && TimeSpan.TryParseExact(s, "c", null, out var ts))
            return ts;
        if (value is TimeSpan direct) // backward compat
            return direct;

        return null;
    }
}
