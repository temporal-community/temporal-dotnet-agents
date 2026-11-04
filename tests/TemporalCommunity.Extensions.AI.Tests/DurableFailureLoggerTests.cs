using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableFailureLoggerTests
{
    private const string SecretMarker = "synthetic-secret-marker";

    [Fact]
    public void LogException_CaptureDisabled_UsesSanitizedErrorAndDetailedDebug()
    {
        using var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var exception = CreateProviderFailure();

        DurableFailureLogger.LogException(
            loggerFactory.CreateLogger("test"),
            exception,
            captureTelemetryContent: false,
            operation: "InvokeFunction",
            context: "function lookup_customer",
            fallbackErrorType: "DurableToolInvocationFailed");

        var error = Assert.Single(provider.Logs, log => log.Level == LogLevel.Error);
        Assert.Null(error.Exception);
        Assert.Contains("InvokeFunction", error.Message);
        Assert.Contains("lookup_customer", error.Message);
        Assert.Contains("HttpRequestException", error.Message);
        Assert.Contains("DurableToolInvocationFailed", error.Message);
        Assert.Contains("400", error.Message);
        Assert.DoesNotContain(SecretMarker, error.Message, StringComparison.Ordinal);

        var debug = Assert.Single(provider.Logs, log => log.Level == LogLevel.Debug);
        Assert.Same(exception, debug.Exception);
        Assert.Contains(SecretMarker, debug.Exception!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LogException_CaptureEnabled_LogsFullExceptionAtErrorOnly()
    {
        using var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var exception = CreateProviderFailure();

        DurableFailureLogger.LogException(
            loggerFactory.CreateLogger("test"),
            exception,
            captureTelemetryContent: true,
            operation: "InvokeFunction",
            context: "function lookup_customer");

        var error = Assert.Single(provider.Logs, log => log.Level == LogLevel.Error);
        Assert.Same(exception, error.Exception);
        Assert.Contains(SecretMarker, error.Exception!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(provider.Logs, log => log.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task GetResponseAsync_LogsStreamingFailureOnlyOncePerAttempt()
    {
        using var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        using var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.AI.IChatClient>(new FailingChatClient())
            .AddSingleton(new DurableExecutionOptions { CaptureTelemetryContent = false })
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory);
        var environment = new Temporalio.Testing.ActivityEnvironment
        {
            PayloadConverter = DurableAIDataConverter.Instance.PayloadConverter,
        };

        await Assert.ThrowsAsync<ApplicationFailureException>(
            () => environment.RunAsync(() => activities.GetResponseAsync(new DurableChatInput
            {
                Messages = [new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User, "hello")],
                ConversationId = "safe-conversation",
                TurnNumber = 7,
            })));

        var error = Assert.Single(provider.Logs, log =>
            log.Level == LogLevel.Error
            && log.Category.Contains(nameof(DurableChatActivities), StringComparison.Ordinal));
        Assert.Null(error.Exception);
        Assert.Contains("safe-conversation", error.Message);
        Assert.Contains("turn 7", error.Message);
        Assert.Contains("LlmNonRetryable", error.Message);
        Assert.DoesNotContain(SecretMarker, error.Message, StringComparison.Ordinal);
        Assert.Single(provider.Logs, log =>
            log.Level == LogLevel.Debug
            && log.Category.Contains(nameof(DurableChatActivities), StringComparison.Ordinal)
            && log.EventId.Name == "LogActivityFailureDetails");
    }

    private static ApplicationFailureException CreateProviderFailure()
    {
        var providerException = new HttpRequestException(
            SecretMarker,
            inner: null,
            statusCode: HttpStatusCode.BadRequest);
        return new ApplicationFailureException(
            "bounded failure",
            providerException,
            errorType: "DurableToolInvocationFailed");
    }

    private sealed class FailingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException(
                SecretMarker,
                inner: null,
                statusCode: HttpStatusCode.BadRequest);

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new HttpRequestException(
                SecretMarker,
                inner: null,
                statusCode: HttpStatusCode.BadRequest);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
