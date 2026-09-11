// HARNESS for docs/how-to/MAF/testing-agents.md § "IntegrationTestFixture Pattern".
//
// This one is a class declaration rather than a statement body, so there is no wrapper method — the
// snippet IS the type. `TestEnvironmentHelper` and `EchoChatClient` are harness stand-ins with the
// signatures the snippet binds against (see ../../Harness/HarnessServices.cs).
//
// Not a test: this project has no test SDK and is never discovered or executed. xunit is referenced
// only so `IAsyncLifetime` resolves, and its analyzers are excluded in the csproj for that reason.
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using TemporalCommunity.Extensions.Agents;
using Xunit;
using DocSnippets.Harness;

namespace DocSnippets.TestingAgents;

// BEGIN SNIPPET docs/how-to/MAF/testing-agents.md#integrationtestfixture-pattern (lines 303-356)
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private IHost? _host;

    public const string TaskQueue = "integration-test-agents";
    public WorkflowEnvironment Environment { get; private set; } = null!;
    public ITemporalClient Client => Environment.Client;
    public AIAgent AgentProxy { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Use TestEnvironmentHelper instead of bare WorkflowEnvironment.StartLocalAsync()
        // because EnableSearchAttributes defaults to true. TestEnvironmentHelper pre-registers the
        // three custom search attributes (AgentName, SessionCreatedAt, TurnCount) that
        // AgentWorkflow upserts when search attributes are enabled. Without them, the
        // workflow fails at runtime with an opaque "unexpected workflow task failure".
        // Bare WorkflowEnvironment.StartLocalAsync() is sufficient only when the test
        // explicitly sets EnableSearchAttributes = false.
        Environment = await TestEnvironmentHelper.StartLocalAsync();

        _host = BuildHost();
        await _host.StartAsync();

        AgentProxy = _host.Services.GetTemporalAgentProxy("EchoAgent");
    }

    public IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Environment.Client);

        builder.Services.AddChatClient(new EchoChatClient());

        builder.Services
            .AddHostedTemporalWorker(TaskQueue)
            .AddTemporalAgents(options =>
                options.AddDurableAgent("EchoAgent", agent =>
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>()));

        return builder.Build();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await Environment.ShutdownAsync();
    }
}
// END SNIPPET docs/how-to/MAF/testing-agents.md#integrationtestfixture-pattern
