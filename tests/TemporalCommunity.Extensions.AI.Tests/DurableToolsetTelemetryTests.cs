using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

[Collection(nameof(DurableToolsetTelemetryTests))]
public sealed class DurableToolsetTelemetryTests
{
    [Fact]
    public async Task ResolverDiagnostics_AreSafeWithoutListenersAndInstrumentsAreRegisteredOnce()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var firstResolver = CreateResolver(loggerFactory);
        var secondResolver = CreateResolver(loggerFactory);

        await firstResolver.ResolveDurableToolsetsAsync(new() { ToolsetIds = ["safe-toolset"] });

        using var capture = new TelemetryCapture();
        await firstResolver.ResolveDurableToolsetsAsync(new() { ToolsetIds = ["safe-toolset"] });
        await secondResolver.ResolveDurableToolsetsAsync(new() { ToolsetIds = ["safe-toolset"] });

        Assert.Equal(
            [
                "temporal.ai.toolset.resolver.attempts",
                "temporal.ai.toolset.resolver.selected_functions",
                "temporal.ai.toolset.resolver.selected_toolsets",
                "temporal.ai.toolset.validation.rejections",
            ],
            capture.PublishedInstrumentNames.Order(StringComparer.Ordinal));
        Assert.Equal(2, capture.Measurements.Count(measurement =>
            measurement.Instrument == "temporal.ai.toolset.resolver.attempts"));
    }

    [Fact]
    public async Task ResolverSuccess_EmitsBoundedSpanMetricsAndStructuredLogs()
    {
        using var capture = new TelemetryCapture();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        var activities = CreateResolver(loggerFactory);

        var manifest = await activities.ResolveDurableToolsetsAsync(new()
        {
            ToolsetIds = ["safe-toolset"],
        });

        Assert.Single(manifest.Members);
        AssertMeasurement(capture, "temporal.ai.toolset.resolver.attempts", 1, "outcome", "success");
        AssertMeasurement(capture, "temporal.ai.toolset.resolver.selected_toolsets", 1);
        AssertMeasurement(capture, "temporal.ai.toolset.resolver.selected_functions", 1);
        Assert.DoesNotContain(capture.Measurements,
            measurement => measurement.Instrument == "temporal.ai.toolset.validation.rejections");
        var span = Assert.Single(capture.Activities);
        Assert.Equal(DurableChatTelemetry.ToolsetResolveSpanName, span.DisplayName);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal("success", span.GetTagItem("outcome"));
        Assert.Null(span.GetTagItem(DurableChatTelemetry.OperationNameAttribute));
        AssertSafeTelemetry(capture);
        AssertLog(logs, 20, "LogToolsetResolverStarted", LogLevel.Debug);
        AssertLog(logs, 21, "LogToolsetResolverCompleted", LogLevel.Debug);
    }

    [Fact]
    public async Task ResolverFailure_EmitsFailureAndRejectionWithoutCountHistograms()
    {
        using var capture = new TelemetryCapture();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        var activities = CreateResolver(loggerFactory);

        await Assert.ThrowsAnyAsync<Exception>(() => activities.ResolveDurableToolsetsAsync(new()
        {
            ToolsetIds = ["safe-toolset", "safe-toolset"],
        }));

        AssertMeasurement(capture, "temporal.ai.toolset.resolver.attempts", 1, "outcome", "failure");
        AssertMeasurement(
            capture,
            "temporal.ai.toolset.validation.rejections",
            1,
            "reason",
            DurableToolsetValidationReasons.DuplicateSelection);
        Assert.DoesNotContain(capture.Measurements, measurement =>
            measurement.Instrument is "temporal.ai.toolset.resolver.selected_toolsets"
                or "temporal.ai.toolset.resolver.selected_functions");
        var span = Assert.Single(capture.Activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("failure", span.GetTagItem("outcome"));
        Assert.Equal(DurableToolsetValidationReasons.DuplicateSelection, span.GetTagItem("reason"));
        AssertSafeTelemetry(capture);
        AssertLog(logs, 20, "LogToolsetResolverStarted", LogLevel.Debug);
        AssertLog(logs, 22, "LogToolsetResolverFailed", LogLevel.Warning);
    }

    [Fact]
    public async Task ActivityValidationAndWorkflowNarrowing_UseReservedLogContracts()
    {
        using var capture = new TelemetryCapture();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        var declaration = DurableFunctionDeclarationSnapshot.Create(
            AIFunctionFactory.Create(() => "ok", "safe_tool").AsDeclarationOnly());
        var memberIdentity = DurableToolsetMemberIdentityFingerprint.Create(
            "safe-toolset",
            "missing-activation",
            declaration);
        var manifestFingerprint = $"tai-toolset-v1:{new string('0', 64)}";
        var input = new DurableFunctionInput
        {
            FunctionName = declaration.Name,
            Declaration = declaration,
            ToolsetId = "safe-toolset",
            ActivationKey = "missing-activation",
            MemberIdentityFingerprint = memberIdentity,
            ManifestFingerprint = manifestFingerprint,
            AuthorityBindingFingerprint = DurableToolsetAuthorityBindingFingerprint.Create(
                manifestFingerprint,
                memberIdentity),
        };
        var functionActivities = new DurableFunctionActivities(
            new Dictionary<string, AIFunction>(),
            loggerFactory);

        await Assert.ThrowsAnyAsync<Exception>(() => new ActivityEnvironment().RunAsync(
            () => functionActivities.InvokeFunctionAsync(input)));
        loggerFactory.CreateLogger("workflow-test").LogToolsetNarrowingRejected(
            DurableToolsetValidationReasons.UnknownToolset);

        AssertMeasurement(
            capture,
            "temporal.ai.toolset.validation.rejections",
            1,
            "reason",
            DurableToolsetValidationReasons.ManifestMismatch);
        AssertLog(logs, 23, "LogToolsetValidationRejected", LogLevel.Warning);
        AssertLog(logs, 24, "LogToolsetNarrowingRejected", LogLevel.Warning);
    }

    [Theory]
    [InlineData(DurableToolsetValidationReasons.UnknownToolset)]
    [InlineData(DurableToolsetValidationReasons.DuplicateSelection)]
    [InlineData(DurableToolsetValidationReasons.NameCollision)]
    [InlineData(DurableToolsetValidationReasons.InvalidManifestVersion)]
    [InlineData(DurableToolsetValidationReasons.AuthorityMismatch)]
    [InlineData(DurableToolsetValidationReasons.InvalidDeclaration)]
    [InlineData(DurableToolsetValidationReasons.InvalidPolicy)]
    public async Task Resolver_ClassifiesEachBoundedRejectionReason(string expectedReason)
    {
        using var capture = new TelemetryCapture();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var (activities, request) = CreateRejectedResolver(expectedReason, loggerFactory);

        await Assert.ThrowsAnyAsync<Exception>(
            () => activities.ResolveDurableToolsetsAsync(request));

        AssertMeasurement(
            capture,
            "temporal.ai.toolset.validation.rejections",
            1,
            "reason",
            expectedReason);
    }

    private static DurableToolsetActivities CreateResolver(ILoggerFactory loggerFactory)
    {
        var function = AIFunctionFactory.Create(() => "ok", "safe_tool");
        var registration = new DurableToolsetRegistration("safe-toolset", false);
        registration.Add(new DurableRegisteredTool(
            DurableFunctionDeclarationSnapshot.Create(function.AsDeclarationOnly()),
            new DurableChatToolOptions(),
            function,
            ActivationFactory: null));
        return new DurableToolsetActivities(
            new DurableToolsetCatalog(
                [registration],
                new DurableExecutionOptions { TaskQueue = "telemetry-test" }),
            loggerFactory);
    }

    private static (DurableToolsetActivities Activities, DurableToolsetResolutionRequest Request)
        CreateRejectedResolver(string reason, ILoggerFactory loggerFactory)
    {
        var function = AIFunctionFactory.Create(() => "ok", "safe_tool");
        var declaration = DurableFunctionDeclarationSnapshot.Create(function.AsDeclarationOnly());
        var registrations = new List<DurableToolsetRegistration>();
        var request = new DurableToolsetResolutionRequest { ToolsetIds = ["safe-toolset"] };

        switch (reason)
        {
            case DurableToolsetValidationReasons.UnknownToolset:
                break;
            case DurableToolsetValidationReasons.DuplicateSelection:
                registrations.Add(CreateRegistration("safe-toolset", declaration, function));
                request = request with { ToolsetIds = ["safe-toolset", "safe-toolset"] };
                break;
            case DurableToolsetValidationReasons.NameCollision:
                registrations.Add(CreateRegistration("safe-toolset", declaration, function));
                registrations.Add(CreateRegistration("other", declaration, function));
                request = request with { ToolsetIds = ["safe-toolset", "other"] };
                break;
            case DurableToolsetValidationReasons.InvalidManifestVersion:
                request = request with { ResolutionVersion = 0 };
                break;
            case DurableToolsetValidationReasons.AuthorityMismatch:
                request = request with { UseWorkerDefaults = true };
                break;
            case DurableToolsetValidationReasons.InvalidDeclaration:
                registrations.Add(CreateRegistration(
                    "safe-toolset",
                    declaration with { JsonSchemaFingerprint = new string('0', 64) },
                    function));
                break;
            case DurableToolsetValidationReasons.InvalidPolicy:
                var invalid = new DurableToolsetRegistration("safe-toolset", false);
                invalid.Add(new DurableRegisteredTool(
                    declaration,
                    new DurableChatToolOptions { StartToCloseTimeout = TimeSpan.Zero },
                    function,
                    ActivationFactory: null));
                registrations.Add(invalid);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return (
            new DurableToolsetActivities(
                new DurableToolsetCatalog(
                    registrations,
                    new DurableExecutionOptions { TaskQueue = "telemetry-test" }),
                loggerFactory),
            request);
    }

    private static DurableToolsetRegistration CreateRegistration(
        string id,
        DurableFunctionDeclarationSnapshot declaration,
        AIFunction function)
    {
        var registration = new DurableToolsetRegistration(id, false);
        registration.Add(new DurableRegisteredTool(
            declaration,
            new DurableChatToolOptions(),
            function,
            ActivationFactory: null));
        return registration;
    }

    private static void AssertMeasurement(
        TelemetryCapture capture,
        string instrument,
        long value,
        string? tagName = null,
        string? tagValue = null)
    {
        var measurement = Assert.Single(capture.Measurements, item =>
            item.Instrument == instrument && item.Value == value);
        if (tagName is not null)
        {
            Assert.Contains(measurement.Tags, tag =>
                tag.Key == tagName && Equals(tag.Value, tagValue));
        }
    }

    private static void AssertSafeTelemetry(TelemetryCapture capture)
    {
        var forbidden = new[]
        {
            "safe-toolset", "safe_tool", "conversation", "tenant", "tai-toolset-v1:",
            "jsonSchema", "requestData", "turnState",
        };
        var text = string.Join("|", capture.Measurements.SelectMany(item => item.Tags)
            .Select(tag => $"{tag.Key}={tag.Value}")) + "|" +
            string.Join("|", capture.Activities.SelectMany(activity => activity.Tags)
                .Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(forbidden, value => text.Contains(value, StringComparison.Ordinal));
    }

    private static void AssertLog(
        CapturingLoggerProvider logs,
        int eventId,
        string eventName,
        LogLevel level)
    {
        var entry = Assert.Single(logs.Logs, log => log.EventId.Id == eventId);
        Assert.Equal(eventName, entry.EventId.Name);
        Assert.Equal(level, entry.Level);
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener activityListener;
        private readonly MeterListener meterListener;

        internal List<Activity> Activities { get; } = [];
        internal List<Measurement> Measurements { get; } = [];
        internal HashSet<string> PublishedInstrumentNames { get; } = [];

        internal TelemetryCapture()
        {
            activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == DurableChatTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Activities.Add(activity),
            };
            ActivitySource.AddActivityListener(activityListener);

            meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == DurableChatTelemetry.MeterName)
                    {
                        Assert.True(PublishedInstrumentNames.Add(instrument.Name),
                            $"Instrument '{instrument.Name}' was published more than once.");
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Add(new Measurement(instrument.Name, value, tags.ToArray())));
            meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
                Measurements.Add(new Measurement(instrument.Name, value, tags.ToArray())));
            meterListener.Start();
        }

        public void Dispose()
        {
            meterListener.Dispose();
            activityListener.Dispose();
        }
    }

    private sealed record Measurement(
        string Instrument,
        long Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}

[CollectionDefinition(nameof(DurableToolsetTelemetryTests), DisableParallelization = true)]
public sealed class DurableToolsetTelemetryTestCollection;
