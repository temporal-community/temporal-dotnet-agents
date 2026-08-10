using System.Security.Cryptography;
using System.Text;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableToolIdempotencyKey
{
    internal const int CurrentVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Domain = StrictUtf8.GetBytes(
        "TemporalCommunity.Extensions.AI/IdempotencyKey/v1\0");

    public static string Create(
        int version,
        string @namespace,
        string workflowId,
        string workflowRunId,
        string activityId)
    {
        if (version != CurrentVersion)
        {
            throw new ApplicationFailureException(
                $"Unsupported durable tool idempotency-key version '{version}'.",
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }

        try
        {
            using var stream = new MemoryStream();
            stream.Write(Domain, 0, Domain.Length);
            WriteComponent(stream, @namespace);
            WriteComponent(stream, workflowId);
            WriteComponent(stream, workflowRunId);
            WriteComponent(stream, activityId);
            var bytes = stream.ToArray();
#if NET10_0_OR_GREATER
            var hash = SHA256.HashData(bytes);
#else
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(bytes);
#endif
            var result = new StringBuilder("tai-v1:", 7 + (hash.Length * 2));
            foreach (var value in hash)
            {
                result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return result.ToString();
        }
        catch (EncoderFallbackException exception)
        {
            throw new ApplicationFailureException(
                "Durable tool activity identity contains invalid UTF-16 data.",
                exception,
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }
    }

    private static void WriteComponent(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        if ((ulong)bytes.Length > uint.MaxValue)
        {
            throw new DurableConfigurationException("An idempotency-key component exceeds UInt32 length.");
        }

        var length = (uint)bytes.Length;
        stream.WriteByte((byte)(length >> 24));
        stream.WriteByte((byte)(length >> 16));
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        stream.Write(bytes, 0, bytes.Length);
    }
}
