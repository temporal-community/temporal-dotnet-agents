using Microsoft.Extensions.AI;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableTelemetryContentCapturePolicy
{
    private const string CaptureEnvironmentVariable =
        "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";
    private static readonly bool EnvironmentDefaultEnabled = IsEnvironmentDefaultEnabled();

    internal static bool Resolve(DurableExecutionOptions? options) =>
        options?.CaptureTelemetryContent ?? EnvironmentDefaultEnabled;

    internal static void ConfigureChatClient(
        IChatClient chatClient,
        DurableExecutionOptions? options)
    {
        var mode = options?.InferenceTelemetryMode
            ?? DurableInferenceTelemetryMode.ApplicationOwned;
        var captureOverride = options?.CaptureTelemetryContent;
        var defaultCapture = EnvironmentDefaultEnabled;
        var wrappers = AgentChainWalker.WalkChatClient(chatClient)
            .OfType<OpenTelemetryChatClient>()
            .Take(2)
            .ToArray();

        if (wrappers.Length > 1)
        {
            throw ConfigurationFailure(
                "Only one discoverable MEAI OpenTelemetryChatClient is supported. " +
                "Remove duplicate inference instrumentation.");
        }

        if (mode == DurableInferenceTelemetryMode.RequireVisibleOpenTelemetryClient
            && wrappers.Length != 1)
        {
            throw ConfigurationFailure(
                "InferenceTelemetryMode requires exactly one discoverable MEAI " +
                "OpenTelemetryChatClient in the resolved chat-client chain.");
        }

        if (mode == DurableInferenceTelemetryMode.TrustOpaqueApplicationInstrumentation
            && captureOverride is null)
        {
            throw ConfigurationFailure(
                "CaptureTelemetryContent must be explicitly set when trusting an opaque " +
                "application-owned telemetry chain.");
        }

        if (wrappers.Length == 0)
        {
            return;
        }

        var wrapper = wrappers[0];
        if (captureOverride is bool explicitCapture)
        {
            wrapper.EnableSensitiveData = explicitCapture;
        }
        else if (wrapper.EnableSensitiveData != defaultCapture)
        {
            throw ConfigurationFailure(
                "The MEAI OpenTelemetryChatClient content-capture setting differs from the " +
                "MEAI environment default. Set DurableExecutionOptions.CaptureTelemetryContent " +
                "to keep inference and tool telemetry consistent.");
        }
    }

    private static bool IsEnvironmentDefaultEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(CaptureEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static ApplicationFailureException ConfigurationFailure(string message) =>
        new(
            message,
            errorType: "DurableInferenceTelemetryConfigurationInvalid",
            nonRetryable: true);
}
