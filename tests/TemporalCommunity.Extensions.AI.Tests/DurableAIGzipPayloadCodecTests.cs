using System.Buffers;
using System.Text;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableAIGzipPayloadCodecTests
{
    private const string V1EncodedDataBase64 =
        "H4sIAAAAAAAEE+IS4+JIzUvOT8nMSxfiyirOz9MvyEnMzANMqIlJqWKEAyUAnD3j2B0BAAA=";

    [Fact]
    public async Task BelowThreshold_PassesThroughSamePayload()
    {
        var payload = CreatePayload("small");
        var codec = CreateCodec(minimumSize: payload.CalculateSize() + 1);

        var encoded = Assert.Single(await codec.EncodeAsync([payload]));

        Assert.Same(payload, encoded);
    }

    [Fact]
    public async Task CompressiblePayload_RoundTripsCompletePayloadAndMetadata()
    {
        var payload = CreatePayload(new string('x', 16_384));
        payload.Metadata["custom"] = ByteString.CopyFromUtf8("retained");
        var originalBytes = payload.ToByteArray();
        var codec = CreateCodec();

        var encoded = Assert.Single(await codec.EncodeAsync([payload]));
        var decoded = Assert.Single(await codec.DecodeAsync([encoded]));

        Assert.NotSame(payload, encoded);
        Assert.Equal(
            DurableAIGzipPayloadCodec.EncodingValue,
            encoded.Metadata[DurableAIGzipPayloadCodec.EncodingMetadataKey].ToStringUtf8());
        Assert.Equal(originalBytes, decoded.ToByteArray());
    }

    [Fact]
    public async Task VersionOneCompatibilityVector_DecodesAndReencodesWithoutWireChanges()
    {
        var original = CreatePayload(new string('x', 256));
        var encoded = CreateEncodedPayload(Convert.FromBase64String(V1EncodedDataBase64));
        var codec = CreateCodec();

        var decoded = Assert.Single(await codec.DecodeAsync([encoded]));
        var reencoded = Assert.Single(await codec.EncodeAsync([original]));

        Assert.Equal(
            new string('x', 256),
            DurableAIDataConverter.Instance.PayloadConverter.ToValue<string>(decoded));
        Assert.Equal(V1EncodedDataBase64, Convert.ToBase64String(reencoded.Data.ToByteArray()));
    }

    [Fact]
    public async Task IncompressiblePayload_WithoutRequiredSavings_PassesThrough()
    {
        var random = new Random(1729);
        var bytes = new byte[32_768];
        random.NextBytes(bytes);
        var payload = new Payload { Data = ByteString.CopyFrom(bytes) };
        var codec = CreateCodec(minimumSavingsRatio: 0.05);

        var encoded = Assert.Single(await codec.EncodeAsync([payload]));

        Assert.Same(payload, encoded);
    }

    [Fact]
    public async Task AlreadyEncodedPayload_IsNotEncodedAgain()
    {
        var codec = CreateCodec();
        var encoded = Assert.Single(await codec.EncodeAsync([CreatePayload(new string('x', 8192))]));

        var second = Assert.Single(await codec.EncodeAsync([encoded]));

        Assert.Same(encoded, second);
    }

    [Fact]
    public async Task UnknownLibraryVersion_FailsWithStableCategory()
    {
        var payload = new Payload { Data = ByteString.CopyFromUtf8("data") };
        payload.Metadata[DurableAIGzipPayloadCodec.EncodingMetadataKey] =
            ByteString.CopyFromUtf8("binary/gzip-temporal-ai-v2");
        var codec = CreateCodec();

        var exception = await Assert.ThrowsAsync<DurableAIPayloadCodecException>(
            () => codec.DecodeAsync([payload]));

        Assert.Equal(DurableAIPayloadCodecError.UnsupportedVersion, exception.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-gzip")]
    public async Task CorruptPayload_FailsWithStableCategory(string data)
    {
        var payload = CreateEncodedPayload(Encoding.UTF8.GetBytes(data));
        var codec = CreateCodec();

        var exception = await Assert.ThrowsAsync<DurableAIPayloadCodecException>(
            () => codec.DecodeAsync([payload]));

        Assert.Equal(DurableAIPayloadCodecError.CorruptPayload, exception.Error);
    }

    [Fact]
    public async Task CorruptPayload_ReturnsAndClearsRentedBuffer()
    {
        var pool = new TrackingArrayPool();
        var codec = CreateCodec(pool);
        var payload = CreateEncodedPayload(Encoding.UTF8.GetBytes("not-gzip"));

        var exception = await Assert.ThrowsAsync<DurableAIPayloadCodecException>(
            () => codec.DecodeAsync([payload]));

        Assert.Equal(DurableAIPayloadCodecError.CorruptPayload, exception.Error);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        var buffer = Assert.IsType<byte[]>(pool.Buffer);
        Assert.Same(buffer, pool.ReturnedBuffer);
        Assert.True(pool.ClearArrayRequested);
        Assert.DoesNotContain(buffer, value => value != 0);
    }

    [Fact]
    public async Task EncodedLimit_IsCheckedBeforeDecompression()
    {
        var payload = CreateEncodedPayload(new byte[65]);
        var codec = CreateCodec(maximumEncodedSize: 64);

        var exception = await Assert.ThrowsAsync<DurableAIPayloadCodecException>(
            () => codec.DecodeAsync([payload]));

        Assert.Equal(DurableAIPayloadCodecError.EncodedPayloadTooLarge, exception.Error);
    }

    [Fact]
    public async Task DecodedLimit_StopsExpansion()
    {
        var encoder = CreateCodec(maximumDecodedSize: 64 * 1024);
        var encoded = Assert.Single(await encoder.EncodeAsync([
            CreatePayload(new string('x', 16_384)),
        ]));
        var boundedDecoder = CreateCodec(maximumDecodedSize: 1024);

        var exception = await Assert.ThrowsAsync<DurableAIPayloadCodecException>(
            () => boundedDecoder.DecodeAsync([encoded]));

        Assert.Equal(DurableAIPayloadCodecError.DecodedPayloadTooLarge, exception.Error);
    }

    [Fact]
    public async Task Collection_RetainsOrderAndCount()
    {
        var first = CreatePayload(new string('a', 4096));
        var second = CreatePayload("small");
        var codec = CreateCodec(minimumSize: 512);

        var encoded = (await codec.EncodeAsync([first, second])).ToArray();
        var decoded = (await codec.DecodeAsync(encoded)).ToArray();

        Assert.Equal(2, decoded.Length);
        Assert.Equal(first.ToByteArray(), decoded[0].ToByteArray());
        Assert.Same(second, encoded[1]);
        Assert.Same(second, decoded[1]);
    }

    [Fact]
    public void CreateDataConverter_PreservesMeaiConverterAndSuppliedCodec()
    {
        var codec = CreateCodec();

        var converter = DurableAIDataConverter.CreateDataConverter(codec);

        Assert.Same(codec, converter.PayloadCodec);
        Assert.NotSame(DurableAIDataConverter.Instance, converter);
        Assert.Null(DurableAIDataConverter.Instance.PayloadCodec);
    }

    [Fact]
    public async Task ReaderWithoutCodec_FailsInsteadOfReturningDefaultValue()
    {
        var codec = CreateCodec();
        var encoded = Assert.Single(await codec.EncodeAsync([CreatePayload(new string('x', 8192))]));

        Assert.ThrowsAny<Exception>(() =>
            DurableAIDataConverter.Instance.PayloadConverter.ToValue<string>(encoded));
    }

    [Fact]
    public async Task ApplicationOwnedComposition_UsesDeclaredEncodeAndReverseDecodeOrder()
    {
        var calls = new List<string>();
        var gzip = CreateCodec();
        var outer = new RecordingCodec("outer", calls);
        var chain = new TestCodecChain(gzip, outer);
        var payload = CreatePayload(new string('x', 8192));

        var encoded = await chain.EncodeAsync([payload]);
        var decoded = await chain.DecodeAsync(encoded);

        Assert.Equal(["outer.encode", "outer.decode"], calls);
        Assert.Equal(payload.ToByteArray(), Assert.Single(decoded).ToByteArray());
    }

    [Fact]
    public void InvalidBounds_AreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAIGzipPayloadCodec(
            new DurableAIGzipPayloadCodecOptions { MinimumPayloadSizeBytes = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAIGzipPayloadCodec(
            new DurableAIGzipPayloadCodecOptions { MaximumEncodedPayloadSizeBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAIGzipPayloadCodec(
            new DurableAIGzipPayloadCodecOptions { MaximumDecodedPayloadSizeBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAIGzipPayloadCodec(
            new DurableAIGzipPayloadCodecOptions { MinimumCompressionSavingsRatio = 1 }));
    }

    private static Payload CreatePayload(string value) =>
        DurableAIDataConverter.Instance.PayloadConverter.ToPayload(value);

    private static Payload CreateEncodedPayload(byte[] data)
    {
        var payload = new Payload { Data = ByteString.CopyFrom(data) };
        payload.Metadata[DurableAIGzipPayloadCodec.EncodingMetadataKey] =
            ByteString.CopyFromUtf8(DurableAIGzipPayloadCodec.EncodingValue);
        return payload;
    }

    private static DurableAIGzipPayloadCodec CreateCodec(
        int minimumSize = 1,
        int maximumEncodedSize = 2 * 1024 * 1024,
        int maximumDecodedSize = 4 * 1024 * 1024,
        double minimumSavingsRatio = 0) => new(new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = minimumSize,
            MaximumEncodedPayloadSizeBytes = maximumEncodedSize,
            MaximumDecodedPayloadSizeBytes = maximumDecodedSize,
            MinimumCompressionSavingsRatio = minimumSavingsRatio,
        });

    private static DurableAIGzipPayloadCodec CreateCodec(ArrayPool<byte> bufferPool) => new(
        new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = 1,
            MinimumCompressionSavingsRatio = 0,
        },
        bufferPool);

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        public byte[]? Buffer { get; private set; }

        public byte[]? ReturnedBuffer { get; private set; }

        public int RentCount { get; private set; }

        public int ReturnCount { get; private set; }

        public bool ClearArrayRequested { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Buffer = new byte[minimumLength];
            Array.Fill(Buffer, (byte)0xA5);
            RentCount++;
            return Buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ReturnCount++;
            ReturnedBuffer = array;
            ClearArrayRequested = clearArray;
            if (clearArray)
            {
                Array.Clear(array);
            }
        }
    }

    private sealed class RecordingCodec(string name, List<string> calls) : IPayloadCodec
    {
        public Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads)
        {
            calls.Add($"{name}.encode");
            return Task.FromResult(payloads);
        }

        public Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads)
        {
            calls.Add($"{name}.decode");
            return Task.FromResult(payloads);
        }
    }

    private sealed class TestCodecChain(params IPayloadCodec[] codecs) : IPayloadCodec
    {
        public async Task<IReadOnlyCollection<Payload>> EncodeAsync(
            IReadOnlyCollection<Payload> payloads)
        {
            foreach (var codec in codecs)
            {
                payloads = await codec.EncodeAsync(payloads);
            }

            return payloads;
        }

        public async Task<IReadOnlyCollection<Payload>> DecodeAsync(
            IReadOnlyCollection<Payload> payloads)
        {
            for (var index = codecs.Length - 1; index >= 0; index--)
            {
                payloads = await codecs[index].DecodeAsync(payloads);
            }

            return payloads;
        }
    }
}
