using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Built-in <see cref="IChatClientDecorator"/> that attaches per-call tags supplied via
/// <see cref="TemporalChatOptionsExtensions.WithChatClientTag(ChatOptions, string, string)"/> to
/// <see cref="Activity.Current"/>. Pre-registered under the key <c>"tags"</c> by
/// <c>AddDurableAI</c> / <c>AddTemporalAgents</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per Q-ChatClientFactory-shape, this decorator is the "80% case" path: covers per-tenant
/// tagging, correlation IDs, and similar OTel context without users registering their own
/// <see cref="IChatClientDecorator"/>. Custom decorators continue to coexist for structural
/// variation (A-B routing, custom retry policies).
/// </para>
/// <para>
/// <b>No-Activity behavior (Q10):</b> when <see cref="Activity.Current"/> is null (no OTel
/// listener registered), the decorator is a silent no-op for the tag-setting. A once-per-process
/// warning is logged the first time this happens so the user gets a discoverable signal that
/// their OTel pipeline isn't wired. Throttled by a static flag so it never spams.
/// </para>
/// </remarks>
internal sealed class TagsChatClientDecorator : IChatClientDecorator
{
    private readonly ILoggerFactory _loggerFactory;

    public TagsChatClientDecorator(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc/>
    public IChatClient Decorate(IChatClient inner, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new TagApplyingChatClient(inner, _loggerFactory);
    }

    /// <summary>
    /// Wrapper that applies <see cref="TemporalChatOptionsExtensions.GetChatClientTags(ChatOptions)"/>
    /// to <see cref="Activity.Current"/> on each call before delegating to <paramref name="inner"/>.
    /// </summary>
    private sealed class TagApplyingChatClient : DelegatingChatClient
    {
        // Throttle flag — log the "no Activity.Current" warning at most once per process per
        // decorator instance. Using static int with Interlocked semantics so the warning fires
        // exactly once even under concurrent dispatches.
        private static int _missingActivityWarned;

        private readonly ILogger<TagsChatClientDecorator> _logger;

        public TagApplyingChatClient(IChatClient inner, ILoggerFactory loggerFactory)
            : base(inner)
        {
            _logger = loggerFactory.CreateLogger<TagsChatClientDecorator>();
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ApplyTags(options);
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ApplyTags(options);
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private void ApplyTags(ChatOptions? options)
        {
            var tags = options.GetChatClientTags();
            if (tags.Count == 0)
            {
                return;
            }

            var current = Activity.Current;
            if (current is null)
            {
                // Throttled once-per-process warning so the user can discover the
                // OTel-not-wired condition without log spam.
                if (Interlocked.Exchange(ref _missingActivityWarned, 1) == 0)
                {
                    var keys = string.Join(", ", tags.Select(t => t.Key));
                    _logger.LogWarning(
                        "WithChatClientTag was used but Activity.Current is null — tags ({TagKeys}) " +
                        "will not be propagated. Ensure your OpenTelemetry pipeline is configured.",
                        keys);
                }
                return;
            }

            foreach (var kvp in tags)
            {
                current.SetTag(kvp.Key, kvp.Value);
            }
        }
    }
}
