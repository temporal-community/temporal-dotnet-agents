using Microsoft.Extensions.AI;
using Temporalio.Converters;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Provides a <see cref="DataConverter"/> configured with <see cref="AIJsonUtilities.DefaultOptions"/>
/// so that MEAI types (<see cref="ChatMessage"/>, <see cref="AIContent"/> subtypes, etc.)
/// round-trip correctly through Temporal's payload converter.
/// </summary>
/// <remarks>
/// <para>
/// MEAI types use a <c>$type</c> discriminator for polymorphic <see cref="AIContent"/> serialization
/// (e.g., <see cref="TextContent"/>, <see cref="FunctionCallContent"/>). The default Temporal
/// <see cref="DefaultPayloadConverter"/> does not include these converters, so MEAI types may
/// lose type information during round-trips through workflow history.
/// </para>
/// <para>
/// Register this converter on the Temporal client or worker options:
/// <code>
/// new TemporalClientConnectOptions
/// {
///     DataConverter = DurableAIDataConverter.Instance
/// }
/// </code>
/// Or when using hosted workers:
/// <code>
/// services.AddHostedTemporalWorker(opts =>
/// {
///     opts.DataConverter = DurableAIDataConverter.Instance;
/// });
/// </code>
/// </para>
/// <para>
/// A data converter belongs to a Temporal client or worker, not to an individual workflow. When a
/// worker uses this converter, every workflow and activity that worker handles uses its payload
/// format, including ordinary application workflows. Every client that starts, signals, queries,
/// or reads results from those workflows must use a compatible converter. Independently constructed
/// <see cref="Temporalio.Client.TemporalClient"/> instances cannot be configured automatically.
/// </para>
/// </remarks>
public static class DurableAIDataConverter
{
    /// <summary>
    /// A <see cref="DataConverter"/> whose JSON serializer uses <see cref="AIJsonUtilities.DefaultOptions"/>,
    /// which correctly handles polymorphic <see cref="AIContent"/> types.
    /// </summary>
    public static DataConverter Instance { get; } = CreateDataConverter();

    /// <summary>
    /// Creates a data converter that preserves MEAI's polymorphic JSON contracts and optionally
    /// applies a caller-supplied Temporal payload codec.
    /// </summary>
    /// <param name="payloadCodec">
    /// Optional payload codec. The caller owns codec composition and must configure every client,
    /// worker, replayer, and operational reader that can read the encoded payloads.
    /// </param>
    /// <returns>A newly-created compatible data converter.</returns>
    public static DataConverter CreateDataConverter(IPayloadCodec? payloadCodec = null) => new(
        new DefaultPayloadConverter(DurableAIJsonUtilities.DefaultOptions),
        new DefaultFailureConverter(),
        payloadCodec);
}
