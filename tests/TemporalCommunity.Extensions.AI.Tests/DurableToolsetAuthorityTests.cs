using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Activities;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableToolsetAuthorityTests
{
    [Fact]
    public async Task InvokeFunction_ValidRecordedBinding_ReachesExactImplementation()
    {
        var setup = CreateSetup();
        var output = await setup.Environment.RunAsync(
            () => setup.Activities.InvokeFunctionAsync(setup.Input));

        Assert.Equal(1, setup.InvocationCount());
        Assert.Equal("ok", Assert.IsType<System.Text.Json.JsonElement>(output.Result).GetString());
    }

    [Theory]
    [InlineData("activation-key")]
    [InlineData("toolset-id")]
    [InlineData("function-name")]
    [InlineData("declaration")]
    [InlineData("manifest-fingerprint")]
    [InlineData("member-fingerprint")]
    [InlineData("authority-binding")]
    public async Task InvokeFunction_ForgedRecordedBinding_DoesNotReachImplementation(string mutation)
    {
        var setup = CreateSetup();
        var input = mutation switch
        {
            "activation-key" => setup.Input with { ActivationKey = "forged" },
            "toolset-id" => setup.Input with { ToolsetId = "forged" },
            "function-name" => setup.Input with { FunctionName = "forged" },
            "declaration" => setup.Input with
            {
                Declaration = setup.Input.Declaration! with
                {
                    JsonSchemaFingerprint = new string('0', 64),
                },
            },
            "manifest-fingerprint" => setup.Input with
            {
                ManifestFingerprint = $"tai-toolset-v1:{new string('0', 64)}",
            },
            "member-fingerprint" => setup.Input with
            {
                MemberIdentityFingerprint = $"tai-tool-member-v1:{new string('0', 64)}",
            },
            "authority-binding" => setup.Input with
            {
                AuthorityBindingFingerprint = $"tai-tool-binding-v1:{new string('0', 64)}",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => setup.Environment.RunAsync(() => setup.Activities.InvokeFunctionAsync(input)));

        Assert.True(failure.NonRetryable);
        Assert.Equal(0, setup.InvocationCount());
    }

    private static TestSetup CreateSetup()
    {
        var invocationCount = 0;
        var function = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref invocationCount);
                return "ok";
            },
            "recorded_tool");
        var registration = new DurableToolsetRegistration("recorded", isImplicitDefault: false);
        registration.Add(new DurableRegisteredTool(
            DurableFunctionDeclarationSnapshot.Create(function.AsDeclarationOnly()),
            new DurableChatToolOptions(),
            function,
            ActivationFactory: null));
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var manifest = new DurableToolsetCatalog([registration], options).Resolve(new()
        {
            ToolsetIds = ["recorded"],
        });
        var member = Assert.Single(manifest.Members);
        var input = new DurableFunctionInput
        {
            FunctionName = member.Declaration.Name,
            Declaration = member.Declaration,
            ToolsetId = member.ToolsetId,
            ActivationKey = member.ActivationKey,
            MemberIdentityFingerprint = member.MemberIdentityFingerprint,
            ManifestFingerprint = manifest.Fingerprint,
            AuthorityBindingFingerprint = DurableToolsetAuthorityBindingFingerprint.Create(
                manifest.Fingerprint,
                member.MemberIdentityFingerprint),
        };
        var activities = new DurableFunctionActivities(
            new Dictionary<string, AIFunction>(),
            toolsetActivationCatalog: new DurableToolsetActivationCatalog([registration]));
        return new TestSetup(
            activities,
            new ActivityEnvironment(),
            input,
            () => Volatile.Read(ref invocationCount));
    }

    private sealed record TestSetup(
        DurableFunctionActivities Activities,
        ActivityEnvironment Environment,
        DurableFunctionInput Input,
        Func<int> InvocationCount);
}
