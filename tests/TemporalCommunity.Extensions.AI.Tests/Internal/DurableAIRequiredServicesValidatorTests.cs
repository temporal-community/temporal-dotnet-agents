using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public sealed class DurableAIRequiredServicesValidatorTests
{
    [Fact]
    public void PostConfigure_RequiresUnkeyedClient_WhenNoDefaultProfileIsSelected()
    {
        var exception = Assert.Throws<DurableConfigurationException>(
            () => Validate(new ServiceCollection(), new DurableExecutionOptions
            {
                TaskQueue = "queue",
            }));

        Assert.Contains("unkeyed IChatClient", exception.Message);
    }

    [Fact]
    public void PostConfigure_AcceptsUnkeyedClient_WithoutConstructingIt()
    {
        var services = new ServiceCollection();
        var constructionCount = 0;
        services.AddSingleton<IChatClient>(_ =>
        {
            constructionCount++;
            return A.Fake<IChatClient>();
        });

        Validate(services, new DurableExecutionOptions { TaskQueue = "queue" });

        Assert.Equal(0, constructionCount);
    }

    [Fact]
    public void PostConfigure_RequiresAndAcceptsConfiguredKeyedProfile()
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "queue",
            DefaultChatClientKey = "primary",
        };
        var missing = Assert.Throws<DurableConfigurationException>(
            () => Validate(new ServiceCollection(), options));
        Assert.Contains("keyed IChatClient profile 'primary'", missing.Message);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("primary", A.Fake<IChatClient>());
        Validate(services, options);
    }

    [Fact]
    public void PostConfigure_ValidatesSeparateSummarizerProfile()
    {
        var options = new DurableExecutionOptions
        {
            TaskQueue = "queue",
            DefaultChatClientKey = "primary",
            DefaultHistorySummarizerClientKey = "summarizer",
        };
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("primary", A.Fake<IChatClient>());

        var exception = Assert.Throws<DurableConfigurationException>(
            () => Validate(services, options));
        Assert.Contains("history summarizer", exception.Message);
        Assert.Contains("summarizer", exception.Message);

        services.AddKeyedSingleton<IChatClient>("summarizer", A.Fake<IChatClient>());
        Validate(services, options);
    }

    [Fact]
    public void PostConfigure_SkipsChatClientRequirement_ForCustomWorkflow()
    {
        Validate(new ServiceCollection(), new DurableExecutionOptions
        {
            TaskQueue = "queue",
            RegisterDefaultWorkflow = false,
        });
    }

    [Fact]
    public void AddDurableAIWorker_RegistersProfileValidator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<IChatClient>("primary", A.Fake<IChatClient>());
        services.AddHostedTemporalWorker("queue")
            .AddDurableAIWorker(options => options.DefaultChatClientKey = "primary");

        using var provider = services.BuildServiceProvider();
        var validator = Assert.Single(
            provider.GetServices<IPostConfigureOptions<TemporalWorkerServiceOptions>>(),
            registration => registration is DurableAIRequiredServicesValidator);

        validator.PostConfigure(name: null, new TemporalWorkerServiceOptions());
    }

    private static void Validate(ServiceCollection services, DurableExecutionOptions options)
    {
        using var provider = services.BuildServiceProvider();
        var validator = new DurableAIRequiredServicesValidator(provider, options);

        validator.PostConfigure(name: null, new TemporalWorkerServiceOptions());
    }
}
