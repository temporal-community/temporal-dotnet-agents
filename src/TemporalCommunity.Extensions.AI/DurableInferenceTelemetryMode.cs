namespace TemporalCommunity.Extensions.AI;

/// <summary>Controls how worker configuration verifies application-owned inference telemetry.</summary>
public enum DurableInferenceTelemetryMode
{
    /// <summary>The application owns inference instrumentation; no wrapper is required.</summary>
    ApplicationOwned,

    /// <summary>Require exactly one discoverable MEAI <c>OpenTelemetryChatClient</c> wrapper.</summary>
    RequireVisibleOpenTelemetryClient,

    /// <summary>
    /// Trust an application-owned opaque chat-client chain whose instrumentation wrapper cannot
    /// be discovered through <c>DelegatingChatClient</c> links.
    /// </summary>
    TrustOpaqueApplicationInstrumentation,
}
