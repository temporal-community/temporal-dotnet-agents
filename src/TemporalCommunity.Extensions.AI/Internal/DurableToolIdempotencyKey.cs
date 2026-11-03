using System.Security.Cryptography;
using System.Text;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableToolIdempotencyKey
{
    internal const int CurrentVersion = 2;
    internal const int ActivityIdentityVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] ActivityIdentityDomain = StrictUtf8.GetBytes(
        "TemporalCommunity.Extensions.AI/IdempotencyKey/v1\0");
    private static readonly byte[] LogicalCallDomain = StrictUtf8.GetBytes(
        "TemporalCommunity.Extensions.AI/IdempotencyKey/v2\0");

    public static string Create(
        int version,
        string @namespace,
        string workflowId,
        string workflowRunId,
        string activityId)
    {
        if (version != ActivityIdentityVersion)
        {
            throw UnsupportedVersion(version);
        }

        return Hash(ActivityIdentityDomain, @namespace, workflowId, workflowRunId, activityId);
    }

    public static string CreateLogicalCall(
        int version,
        string @namespace,
        string workflowId,
        string sessionEpoch,
        string requestId,
        int turnNumber,
        string toolName,
        string? toolCallId,
        int modelIteration,
        int callIndex)
    {
        if (version != CurrentVersion)
        {
            throw UnsupportedVersion(version);
        }

        RequireComponent(@namespace, nameof(@namespace));
        RequireComponent(workflowId, nameof(workflowId));
        RequireComponent(sessionEpoch, nameof(sessionEpoch));
        RequireComponent(requestId, nameof(requestId));
        RequireComponent(toolName, nameof(toolName));
        if (turnNumber < 0)
        {
            throw new ApplicationFailureException(
                "Durable tool turn number cannot be negative.",
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }
        if (modelIteration < 0 || callIndex < 0)
        {
            throw new ApplicationFailureException(
                "Durable tool call position cannot be negative.",
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }

        try
        {
            using var stream = new MemoryStream();
            stream.Write(LogicalCallDomain, 0, LogicalCallDomain.Length);
            WriteComponent(stream, @namespace);
            WriteComponent(stream, workflowId);
            WriteComponent(stream, sessionEpoch);
            WriteComponent(stream, requestId);
            WriteComponent(stream, turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteComponent(stream, toolName);
            WriteComponent(
                stream,
                string.IsNullOrWhiteSpace(toolCallId) ? $"index:{callIndex}" : toolCallId);
            WriteComponent(stream, modelIteration.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteComponent(stream, callIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Hash(stream.ToArray(), "tai-v2:");
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

    private static string Hash(
        byte[] domain,
        string @namespace,
        string workflowId,
        string workflowRunId,
        string activityId)
    {
        RequireComponent(@namespace, nameof(@namespace));
        RequireComponent(workflowId, nameof(workflowId));
        RequireComponent(workflowRunId, nameof(workflowRunId));
        RequireComponent(activityId, nameof(activityId));

        try
        {
            using var stream = new MemoryStream();
            stream.Write(domain, 0, domain.Length);
            WriteComponent(stream, @namespace);
            WriteComponent(stream, workflowId);
            WriteComponent(stream, workflowRunId);
            WriteComponent(stream, activityId);
            return Hash(stream.ToArray(), "tai-v1:");
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

    private static string Hash(byte[] bytes, string prefix)
    {
#if NET10_0_OR_GREATER
        var hash = SHA256.HashData(bytes);
#else
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
#endif
        var result = new StringBuilder(prefix, prefix.Length + (hash.Length * 2));
        foreach (var value in hash)
        {
            result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static ApplicationFailureException UnsupportedVersion(int version) =>
        new(
            $"Unsupported durable tool idempotency-key version '{version}'.",
            errorType: nameof(DurableConfigurationException),
            nonRetryable: true);

    private static void RequireComponent(string value, string name)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        throw new ApplicationFailureException(
            $"Durable tool identity component '{name}' is missing.",
            errorType: nameof(DurableConfigurationException),
            nonRetryable: true);
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
