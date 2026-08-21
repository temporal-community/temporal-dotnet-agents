using System.Buffers;
using System.IO.Compression;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Options for <see cref="DurableAIGzipPayloadCodec"/>.
/// </summary>
public sealed class DurableAIGzipPayloadCodecOptions
{
    /// <summary>Gets or sets the minimum complete Temporal payload size considered for compression.</summary>
    public int MinimumPayloadSizeBytes { get; init; } = 1024;

    /// <summary>Gets or sets the maximum accepted compressed data size.</summary>
    public int MaximumEncodedPayloadSizeBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Gets or sets the maximum accepted restored payload size.</summary>
    public int MaximumDecodedPayloadSizeBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the minimum fractional reduction required before the compressed form is used.
    /// A value of <c>0.05</c> requires at least five percent savings.
    /// </summary>
    public double MinimumCompressionSavingsRatio { get; init; } = 0.05;

    /// <summary>Gets or sets the gzip compression level.</summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;
}

/// <summary>Identifies a stable payload codec failure category.</summary>
public enum DurableAIPayloadCodecError
{
    /// <summary>The encoded payload uses an unsupported library codec version.</summary>
    UnsupportedVersion = 0,

    /// <summary>The compressed payload exceeds the configured encoded-size bound.</summary>
    EncodedPayloadTooLarge = 1,

    /// <summary>The restored payload exceeds the configured decoded-size bound.</summary>
    DecodedPayloadTooLarge = 2,

    /// <summary>The compressed data or restored Temporal payload is malformed.</summary>
    CorruptPayload = 3,
}

/// <summary>Thrown when a library-owned encoded payload cannot be safely decoded.</summary>
public sealed class DurableAIPayloadCodecException : Exception
{
    internal DurableAIPayloadCodecException(
        DurableAIPayloadCodecError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Error = error;

    /// <summary>Gets the stable failure category.</summary>
    public DurableAIPayloadCodecError Error { get; }
}

/// <summary>
/// An opt-in, bounded gzip codec for complete Temporal payloads produced by
/// <see cref="DurableAIDataConverter"/>.
/// </summary>
/// <remarks>
/// Compression is not encryption or authentication. Deploy compatible decoding to every reader
/// before enabling encoding. Payloads below the threshold or without sufficient savings are passed
/// through unchanged.
/// </remarks>
public sealed class DurableAIGzipPayloadCodec : IPayloadCodec
{
    internal const string EncodingMetadataKey = "encoding";
    internal const string EncodingValue = "binary/gzip-temporal-ai-v1";
    private const string EncodingPrefix = "binary/gzip-temporal-ai-";
    private const int CopyBufferSize = 81_920;

    private readonly DurableAIGzipPayloadCodecOptions options;
    private readonly ArrayPool<byte> bufferPool;

    /// <summary>Initializes a new instance of the codec.</summary>
    /// <param name="options">Compression and safety bounds.</param>
    public DurableAIGzipPayloadCodec(DurableAIGzipPayloadCodecOptions options)
        : this(options, ArrayPool<byte>.Shared)
    {
    }

    internal DurableAIGzipPayloadCodec(
        DurableAIGzipPayloadCodecOptions options,
        ArrayPool<byte> bufferPool)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bufferPool);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinimumPayloadSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumEncodedPayloadSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDecodedPayloadSizeBytes);
        if (options.MinimumCompressionSavingsRatio is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MinimumCompressionSavingsRatio,
                "MinimumCompressionSavingsRatio must be at least zero and less than one.");
        }

        this.options = options;
        this.bufferPool = bufferPool;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Payload>> EncodeAsync(
        IReadOnlyCollection<Payload> payloads) =>
        Task.FromResult<IReadOnlyCollection<Payload>>(Transform(payloads, Encode));

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Payload>> DecodeAsync(
        IReadOnlyCollection<Payload> payloads) =>
        Task.FromResult<IReadOnlyCollection<Payload>>(Transform(payloads, Decode));

    private static IReadOnlyCollection<Payload> Transform(
        IReadOnlyCollection<Payload> payloads,
        Func<Payload, Payload> transform)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (payloads.Count == 0)
        {
            return Array.Empty<Payload>();
        }

        var transformed = new Payload[payloads.Count];
        var index = 0;
        foreach (var payload in payloads)
        {
            transformed[index++] = transform(payload ?? throw new ArgumentException(
                "Payload collections cannot contain null entries.",
                nameof(payloads)));
        }

        return transformed;
    }

    private Payload Encode(Payload payload)
    {
        var encoding = GetEncoding(payload);
        if (encoding is not null)
        {
            if (string.Equals(encoding, EncodingValue, StringComparison.Ordinal))
            {
                return payload;
            }

            if (encoding.StartsWith(EncodingPrefix, StringComparison.Ordinal))
            {
                throw CreateUnsupportedVersion(encoding);
            }
        }

        var originalSize = payload.CalculateSize();
        if (originalSize < options.MinimumPayloadSizeBytes)
        {
            return payload;
        }

        if (originalSize > options.MaximumDecodedPayloadSizeBytes)
        {
            throw new DurableAIPayloadCodecException(
                DurableAIPayloadCodecError.DecodedPayloadTooLarge,
                $"Payload size {originalSize} exceeds the configured decoded limit " +
                $"of {options.MaximumDecodedPayloadSizeBytes} bytes.");
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, options.CompressionLevel, leaveOpen: true))
        {
            payload.WriteTo(gzip);
        }

        if (output.Length > options.MaximumEncodedPayloadSizeBytes)
        {
            throw new DurableAIPayloadCodecException(
                DurableAIPayloadCodecError.EncodedPayloadTooLarge,
                $"Compressed payload size {output.Length} exceeds the configured encoded limit " +
                $"of {options.MaximumEncodedPayloadSizeBytes} bytes.");
        }

        var encoded = new Payload
        {
            Data = ByteString.CopyFrom(
                output.GetBuffer(),
                0,
                checked((int)output.Length)),
        };
        encoded.Metadata[EncodingMetadataKey] = ByteString.CopyFromUtf8(EncodingValue);

        var requiredMaximumSize = originalSize * (1 - options.MinimumCompressionSavingsRatio);
        return encoded.CalculateSize() <= requiredMaximumSize ? encoded : payload;
    }

    private Payload Decode(Payload payload)
    {
        var encoding = GetEncoding(payload);
        if (encoding is null || !encoding.StartsWith(EncodingPrefix, StringComparison.Ordinal))
        {
            return payload;
        }

        if (!string.Equals(encoding, EncodingValue, StringComparison.Ordinal))
        {
            throw CreateUnsupportedVersion(encoding);
        }

        if (payload.Data.Length > options.MaximumEncodedPayloadSizeBytes)
        {
            throw new DurableAIPayloadCodecException(
                DurableAIPayloadCodecError.EncodedPayloadTooLarge,
                $"Compressed payload size {payload.Data.Length} exceeds the configured encoded limit " +
                $"of {options.MaximumEncodedPayloadSizeBytes} bytes.");
        }

        try
        {
            using var input = new MemoryStream(payload.Data.ToByteArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var restored = new MemoryStream();
            var buffer = bufferPool.Rent(CopyBufferSize);
            try
            {
                while (true)
                {
                    var read = gzip.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    if (restored.Length + read > options.MaximumDecodedPayloadSizeBytes)
                    {
                        throw new DurableAIPayloadCodecException(
                            DurableAIPayloadCodecError.DecodedPayloadTooLarge,
                            $"Restored payload exceeds the configured decoded limit " +
                            $"of {options.MaximumDecodedPayloadSizeBytes} bytes.");
                    }

                    restored.Write(buffer, 0, read);
                }
            }
            finally
            {
                bufferPool.Return(buffer, clearArray: true);
            }

            if (restored.Length == 0)
            {
                throw new DurableAIPayloadCodecException(
                    DurableAIPayloadCodecError.CorruptPayload,
                    "The gzip payload did not contain a serialized Temporal payload.");
            }

            restored.Position = 0;
            return Payload.Parser.ParseFrom(restored);
        }
        catch (DurableAIPayloadCodecException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new DurableAIPayloadCodecException(
                DurableAIPayloadCodecError.CorruptPayload,
                "The gzip payload is corrupt or does not contain a valid Temporal payload.",
                exception);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new DurableAIPayloadCodecException(
                DurableAIPayloadCodecError.CorruptPayload,
                "The restored data is not a valid Temporal payload.",
                exception);
        }
    }

    private static string? GetEncoding(Payload payload) =>
        payload.Metadata.TryGetValue(EncodingMetadataKey, out var encoding)
            ? encoding.ToStringUtf8()
            : null;

    private static DurableAIPayloadCodecException CreateUnsupportedVersion(string encoding) => new(
        DurableAIPayloadCodecError.UnsupportedVersion,
        $"Payload encoding '{encoding}' is not supported by this codec version.");
}
