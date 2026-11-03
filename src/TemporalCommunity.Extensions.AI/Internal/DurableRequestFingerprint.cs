using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableRequestFingerprint
{
    internal static string ResolveRequestId(string? requestId)
    {
        if (requestId is not null && string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId cannot be empty or whitespace.", nameof(requestId));
        }

        return requestId ?? Guid.NewGuid().ToString("N");
    }

    internal static string CreateChat(
        DurableChatInput input,
        DurableChatWorkflowInput sessionInput)
    {
        if (input.Options?.Tools is { Count: > 0 })
        {
            throw new DurableConfigurationException(
                "ChatOptions.Tools cannot be fingerprinted as durable session input. " +
                "Register durable tools through the session's tool authority instead.");
        }

        return Create(new
        {
            Version = 1,
            input.Messages,
            Options = ChatOptionsSanitizer.PrepareForDurableTransport(input.Options),
            ClientKey = input.ClientKey ?? sessionInput.DefaultChatClientKey,
            Authority = CreateAuthority(sessionInput),
        });
    }

    internal static string CreateTurn<TRequestData, TTurnState>(
        DurableTurnRequest<TRequestData, TTurnState> request,
        DurableChatWorkflowInput sessionInput,
        string? toolsetManifestFingerprint)
    {
        if (request.ChatOptions?.Tools is { Count: > 0 })
        {
            throw new DurableConfigurationException(
                "ChatOptions.Tools cannot be fingerprinted as durable turn input. " +
                "Select tools through DurableTurnOptions.ToolsetIds.");
        }

        return Create(new
        {
            Version = 1,
            request.Messages,
            ChatOptions = ChatOptionsSanitizer.PrepareForDurableTransport(request.ChatOptions),
            ClientKey = sessionInput.DefaultChatClientKey,
            Authority = CreateAuthority(sessionInput, toolsetManifestFingerprint),
            request.RequestData,
            request.InitialTurnState,
            request.Options.DispatchMode,
            request.Options.ToolsetIds,
        });
    }

    internal static string CreateSessionTurn(
        DurableSessionRequest request,
        ChatOptions? chatOptions,
        DurableChatWorkflowInput sessionInput)
    {
        if (chatOptions?.Tools is { Count: > 0 })
        {
            throw new DurableConfigurationException(
                "ChatOptions.Tools cannot be fingerprinted as durable session input. " +
                "Register durable tools through the session's tool authority instead.");
        }

        return Create(new
        {
            Version = 1,
            request.Messages,
            ChatOptions = ChatOptionsSanitizer.PrepareForDurableTransport(chatOptions),
            ClientKey = sessionInput.DefaultChatClientKey,
            Authority = CreateAuthority(sessionInput),
        });
    }

    internal static string Create(object payload)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(payload, DurableAIJsonUtilities.DefaultOptions);
            return $"tai-request-v1:{DurableJsonSchemaFingerprint.Create(json)}";
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new DurableConfigurationException(
                "Durable request input contains a value that cannot be serialized for retry identity.",
                exception);
        }
    }

    internal static object CreateAuthority(
        DurableChatWorkflowInput input,
        string? toolsetManifestFingerprint = null) =>
        new
        {
            ManifestFingerprint = toolsetManifestFingerprint ?? input.ToolsetManifest?.Fingerprint,
            input.ToolDeclarations,
            input.ToolActivityOptions,
            input.InterceptorActivityOptions,
            input.InterceptorToolActivityOptions,
            input.InterceptorSkippedTools,
            input.RequiresApprovalTools,
            input.ToolApprovalTimeouts,
            input.ApprovalTimeout,
        };
}
