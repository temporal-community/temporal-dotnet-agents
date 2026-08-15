using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public sealed class DurableToolsetConfigurationValidatorTests
{
    [Fact]
    public void PostConfigure_UnknownDefaultToolset_FailsAtStartup()
    {
        var validator = CreateValidator(
            new DurableExecutionOptions
            {
                TaskQueue = "test",
                DefaultToolsetIds = ["missing"],
            });

        var exception = Assert.Throws<DurableConfigurationException>(() =>
            validator.PostConfigure(name: null, new TemporalWorkerServiceOptions()));

        Assert.Contains("default durable toolset configuration", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public void PostConfigure_CollidingDefaultToolsets_FailsAtStartup()
    {
        var first = Registration("first", "same_tool");
        var second = Registration("second", "same_tool");
        var validator = CreateValidator(
            new DurableExecutionOptions
            {
                TaskQueue = "test",
                DefaultToolsetIds = ["first", "second"],
            },
            first,
            second);

        Assert.Throws<DurableConfigurationException>(() =>
            validator.PostConfigure(name: null, new TemporalWorkerServiceOptions()));
    }

    [Fact]
    public void PostConfigure_ValidEmptyDefaultSelection_Succeeds()
    {
        var validator = CreateValidator(new DurableExecutionOptions
        {
            TaskQueue = "test",
            DefaultToolsetIds = [],
        });

        validator.PostConfigure(name: null, new TemporalWorkerServiceOptions());
    }

    private static DurableToolsetConfigurationValidator CreateValidator(
        DurableExecutionOptions options,
        params DurableToolsetRegistration[] registrations) =>
        new(new DurableToolsetCatalog(registrations, options));

    private static DurableToolsetRegistration Registration(string id, string functionName)
    {
        var function = AIFunctionFactory.Create(() => "ok", functionName);
        var registration = new DurableToolsetRegistration(id, isImplicitDefault: false);
        registration.Add(new DurableRegisteredTool(
            DurableFunctionDeclarationSnapshot.Create(function.AsDeclarationOnly()),
            new DurableChatToolOptions(),
            function,
            ActivationFactory: null));
        return registration;
    }
}
