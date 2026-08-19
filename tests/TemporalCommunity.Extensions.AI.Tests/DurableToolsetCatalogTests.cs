using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableToolsetCatalogTests
{
    [Fact]
    public void Resolve_DefaultToolset_FreezesOrderedDeclarationsAndPolicy()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI(options =>
        {
            options.ActivityTimeout = TimeSpan.FromMinutes(3);
            options.HeartbeatTimeout = TimeSpan.FromSeconds(20);
            options.ApprovalTimeout = TimeSpan.FromHours(4);
        });
        worker.AddDurableTools(
            AIFunctionFactory.Create((string city) => city, "weather"),
            tool => tool.WithMaxAttempts(2).RequireApproval());
        worker.AddDurableTools(AIFunctionFactory.Create(() => "ok", "status"));
        using var provider = services.BuildServiceProvider();

        var manifest = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            UseWorkerDefaults = true,
        });

        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal(["default"], manifest.ToolsetIds);
        Assert.Equal(["weather", "status"], manifest.Members.Select(m => m.Declaration.Name));
        Assert.Equal("default", manifest.Members[0].ToolsetId);
        Assert.True(manifest.Members[0].RequiresApproval);
        Assert.Equal(2, manifest.Members[0].ToolActivityOptions.RetryPolicy!.MaximumAttempts);
        Assert.Equal(TimeSpan.FromMinutes(3), manifest.Members[1].ToolActivityOptions.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromHours(4), manifest.Members[1].ApprovalTimeout);
        Assert.StartsWith("tai-toolset-v1:", manifest.Fingerprint, StringComparison.Ordinal);
        manifest.Validate();
    }

    [Fact]
    public void Resolve_ExplicitToolsets_PreservesRequestedAndMemberOrder()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();
        worker.AddDurableToolset("Catalog", tools => tools
            .Add(AIFunctionFactory.Create(() => "one", "Find"))
            .Add(AIFunctionFactory.Create(() => "two", "List")));
        worker.AddDurableToolset("catalog", tools => tools
            .Add(AIFunctionFactory.Create(() => "three", "find")));
        using var provider = services.BuildServiceProvider();

        var manifest = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            ToolsetIds = ["catalog", "Catalog"],
        });

        Assert.Equal(["catalog", "Catalog"], manifest.ToolsetIds);
        Assert.Equal(["find", "Find", "List"], manifest.Members.Select(m => m.Declaration.Name));
        Assert.Equal(["catalog", "Catalog", "Catalog"], manifest.Members.Select(m => m.ToolsetId));
    }

    [Fact]
    public void Resolve_OrderedNamedDefaults_ComposesManifestInConfiguredOrder()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI(options =>
            options.DefaultToolsetIds = ["actions", "catalog"]);
        worker.AddDurableToolset("catalog", tools => tools
            .Add(AIFunctionFactory.Create(() => "find", "find")));
        worker.AddDurableToolset("actions", tools => tools
            .Add(AIFunctionFactory.Create(() => "reserve", "reserve")));
        using var provider = services.BuildServiceProvider();

        var manifest = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            UseWorkerDefaults = true,
        });

        Assert.Equal(["actions", "catalog"], manifest.ToolsetIds);
        Assert.Equal(["reserve", "find"], manifest.Members.Select(m => m.Declaration.Name));
    }

    [Fact]
    public void Resolve_RejectsMixingImplicitAndExplicitDefaultConfiguration()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI(options =>
            options.DefaultToolsetIds = ["catalog"]);
        worker.AddDurableTools(AIFunctionFactory.Create(() => "implicit", "implicit"));
        worker.AddDurableToolset("catalog", tools =>
            tools.Add(AIFunctionFactory.Create(() => "find", "find")));
        using var provider = services.BuildServiceProvider();

        AssertNonRetryable(() => provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            UseWorkerDefaults = true,
        }));
    }

    [Fact]
    public void Resolve_RejectsUnknownDuplicateAndCollidingSelections()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();
        worker.AddDurableToolset("one", tools =>
            tools.Add(AIFunctionFactory.Create(() => "one", "same")));
        worker.AddDurableToolset("two", tools =>
            tools.Add(AIFunctionFactory.Create(() => "two", "same")));
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<DurableToolsetCatalog>();

        AssertNonRetryable(() => catalog.Resolve(new() { ToolsetIds = ["missing"] }));
        AssertNonRetryable(() => catalog.Resolve(new() { ToolsetIds = ["one", "one"] }));
        AssertNonRetryable(() => catalog.Resolve(new() { ToolsetIds = ["one", "two"] }));
        AssertNonRetryable(() => catalog.Resolve(new()
        {
            UseWorkerDefaults = true,
            ToolsetIds = [],
        }));
    }

    [Fact]
    public void Resolve_EmptyExplicitSelectionCreatesValidNoToolManifest()
    {
        using var provider = CreateServices()
            .AddHostedTemporalWorker("queue")
            .AddDurableAI()
            .Services
            .BuildServiceProvider();

        var manifest = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            ToolsetIds = [],
        });

        Assert.Empty(manifest.ToolsetIds);
        Assert.Empty(manifest.Members);
        manifest.Validate();
    }

    [Fact]
    public void Narrow_PreservesBaselineOrderAndSupportsNoTools()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();
        worker.AddDurableToolset("first", tools => tools
            .Add(AIFunctionFactory.Create(() => "one", "one")));
        worker.AddDurableToolset("second", tools => tools
            .Add(AIFunctionFactory.Create(() => "two", "two")));
        worker.AddDurableToolset("third", tools => tools
            .Add(AIFunctionFactory.Create(() => "three", "three")));
        using var provider = services.BuildServiceProvider();
        var baseline = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            ToolsetIds = ["first", "second", "third"],
        });

        var narrowed = baseline.Narrow(["third", "first"]);
        var empty = baseline.Narrow([]);

        Assert.Same(baseline, baseline.Narrow(null));
        Assert.Equal(["first", "third"], narrowed.ToolsetIds);
        Assert.Equal(["one", "three"], narrowed.Members.Select(member => member.Declaration.Name));
        Assert.NotEqual(baseline.Fingerprint, narrowed.Fingerprint);
        Assert.Empty(empty.ToolsetIds);
        Assert.Empty(empty.Members);
        empty.Validate();
    }

    [Fact]
    public void Narrow_RejectsDuplicateAndOutOfBaselineSelections()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();
        worker.AddDurableToolset("allowed", tools => tools
            .Add(AIFunctionFactory.Create(() => "ok", "allowed_tool")));
        using var provider = services.BuildServiceProvider();
        var baseline = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            ToolsetIds = ["allowed"],
        });

        AssertNonRetryable(() => baseline.Narrow(["allowed", "allowed"]));
        AssertNonRetryable(() => baseline.Narrow(["missing"]));
    }

    [Fact]
    public void Manifest_RejectsInvalidPolicyEvenWithMatchingTopLevelFingerprint()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();
        worker.AddDurableToolset("allowed", tools => tools
            .Add(AIFunctionFactory.Create(() => "ok", "allowed_tool")));
        using var provider = services.BuildServiceProvider();
        var manifest = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            ToolsetIds = ["allowed"],
        });
        var invalid = manifest with
        {
            Members = [manifest.Members[0] with { ApprovalTimeout = TimeSpan.Zero }],
            Fingerprint = string.Empty,
        };
        invalid = invalid with
        {
            Fingerprint = DurableToolsetManifestFingerprint.Create(invalid),
        };

        AssertNonRetryable(invalid.Validate);
    }

    [Fact]
    public void Manifest_DuplicateSchemaProperty_FailsNonRetryably()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();
        worker.AddDurableToolset("allowed", tools => tools
            .Add(AIFunctionFactory.Create(() => "ok", "allowed_tool")));
        using var provider = services.BuildServiceProvider();
        var manifest = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            ToolsetIds = ["allowed"],
        });
        using var duplicateSchema = JsonDocument.Parse(
            """{"type":"string","type":"number"}""");
        var invalid = manifest with
        {
            Members =
            [
                manifest.Members[0] with
                {
                    Declaration = manifest.Members[0].Declaration with
                    {
                        JsonSchema = duplicateSchema.RootElement.Clone(),
                    },
                },
            ],
        };

        var exception = Assert.Throws<ApplicationFailureException>(invalid.Validate);

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(TemporalCommunity.Extensions.AI.Exceptions.DurableConfigurationException), exception.ErrorType);
    }

    [Fact]
    public void Authority_RejectsCallerDeclarationsCombinedWithWorkerManifest()
    {
        var declaration = DurableFunctionDeclarationSnapshot.Create(
            AIFunctionFactory.Create(() => "ok", "caller_tool").AsDeclarationOnly());
        var input = new DurableChatWorkflowInput
        {
            ToolDeclarations = [declaration],
            ToolsetManifest = CreateEmptyManifest(),
        };

        AssertNonRetryable(() => DurableToolsetAuthority.Resolve(input));
    }

    [Fact]
    public void Manifest_RoundTripsThroughDurableConverterWithoutRuntimeObjects()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();
        worker.AddDurableToolset("catalog", tools =>
            tools.AddDurableToolFactory<SimpleHandler>(nameof(SimpleHandler.Execute)));
        using var provider = services.BuildServiceProvider();
        var manifest = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            ToolsetIds = ["catalog"],
        });

        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var payload = converter.ToPayload(manifest);
        var restored = Assert.IsType<DurableToolsetManifest>(converter.ToValue(
            payload,
            typeof(DurableToolsetManifest)));

        Assert.Equal(manifest.ManifestVersion, restored.ManifestVersion);
        Assert.Equal(manifest.ToolsetIds, restored.ToolsetIds);
        Assert.Equal(
            manifest.Members.Select(member => member.Declaration.Name),
            restored.Members.Select(member => member.Declaration.Name));
        Assert.Equal(manifest.Fingerprint, restored.Fingerprint);
        restored.Validate();
        var json = JsonSerializer.Serialize(manifest, DurableAIJsonUtilities.DefaultOptions);
        Assert.DoesNotContain(nameof(SimpleHandler), json, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Type", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MethodInfo", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IServiceProvider", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_RejectsMissingUnsupportedAndTamperedVersionsOrFingerprint()
    {
        var manifest = CreateEmptyManifest();
        manifest.Validate();

        AssertNonRetryable(() => (manifest with { ManifestVersion = 0 }).Validate());
        AssertNonRetryable(() => (manifest with { ManifestVersion = 2 }).Validate());
        AssertNonRetryable(() => (manifest with { Fingerprint = "tampered" }).Validate());
    }

    [Fact]
    public void ManifestVersionOne_UsesFrozenFingerprintVector()
    {
        var manifest = CreateEmptyManifest();

        Assert.Equal(
            "tai-toolset-v1:60051ad63143350993d6849484391752110cd71d72234847dd6275a93bc5623d",
            manifest.Fingerprint);
    }

    [Fact]
    public void ManifestFingerprint_ChangesWhenAuthorityPolicyChanges()
    {
        var services = CreateServices();
        var worker = services.AddHostedTemporalWorker("queue").AddDurableAI();
        worker.AddDurableTools(AIFunctionFactory.Create(() => "ok", "tool"));
        using var provider = services.BuildServiceProvider();
        var manifest = provider.GetRequiredService<DurableToolsetCatalog>().Resolve(new()
        {
            UseWorkerDefaults = true,
        });
        var changed = manifest with
        {
            Members =
            [
                manifest.Members[0] with
                {
                    RequiresApproval = !manifest.Members[0].RequiresApproval,
                },
            ],
        };

        Assert.NotEqual(
            manifest.Fingerprint,
            DurableToolsetManifestFingerprint.Create(changed));
    }

    [Fact]
    public void ManifestJson_IgnoresAdditivePropertyButDoesNotGuessVersion()
    {
        var manifest = CreateEmptyManifest();
        var json = JsonSerializer.Serialize(manifest, DurableAIJsonUtilities.DefaultOptions);
        json = json.TrimEnd('}') + ",\"futureDiagnostic\":true}";

        var restored = JsonSerializer.Deserialize<DurableToolsetManifest>(
            json,
            DurableAIJsonUtilities.DefaultOptions)!;

        restored.Validate();
        var absentVersion = JsonSerializer.Deserialize<DurableToolsetManifest>(
            "{\"toolsetIds\":[],\"members\":[],\"fingerprint\":\"x\"}",
            DurableAIJsonUtilities.DefaultOptions)!;
        AssertNonRetryable(absentVersion.Validate);
    }

    [Fact]
    public void ResolutionRequestJson_RoundTripsAndAbsentVersionUsesVersionOne()
    {
        var request = new DurableToolsetResolutionRequest
        {
            ToolsetIds = ["catalog", "orders"],
        };

        var json = JsonSerializer.Serialize(request, DurableAIJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<DurableToolsetResolutionRequest>(
            json,
            DurableAIJsonUtilities.DefaultOptions)!;
        var absentVersion = JsonSerializer.Deserialize<DurableToolsetResolutionRequest>(
            "{\"toolsetIds\":[\"catalog\"]}",
            DurableAIJsonUtilities.DefaultOptions)!;

        Assert.Equal(DurableToolsetResolutionRequest.CurrentVersion, restored.ResolutionVersion);
        Assert.Equal(["catalog", "orders"], restored.ToolsetIds);
        Assert.Equal(DurableToolsetResolutionRequest.CurrentVersion, absentVersion.ResolutionVersion);
        Assert.Equal(["catalog"], absentVersion.ToolsetIds);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        return services;
    }

    private static DurableToolsetManifest CreateEmptyManifest()
    {
        var manifest = new DurableToolsetManifest
        {
            ManifestVersion = 1,
            ToolsetIds = [],
            Members = [],
            Fingerprint = string.Empty,
        };
        return manifest with
        {
            Fingerprint = DurableToolsetManifestFingerprint.Create(manifest),
        };
    }

    private static void AssertNonRetryable(Action action)
    {
        var exception = Assert.Throws<ApplicationFailureException>(action);
        Assert.True(exception.NonRetryable);
    }

    public sealed class SimpleHandler
    {
        public string Execute(string value) => value;
    }
}
