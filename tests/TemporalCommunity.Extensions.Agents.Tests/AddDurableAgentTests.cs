using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Tools;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Phase 2 (v0.3 API redesign): coverage for <see cref="TemporalAgentsOptions.AddDurableAgent"/>
/// registration semantics. Validates the four checkpoints listed in Q9 of the plan, plus the
/// introspection wiring that makes durable agents visible to <c>GetRegisteredAgentNames</c> and
/// <c>GetAgentDescriptors</c>.
/// </summary>
public class AddDurableAgentTests
{
    private static IChatClient NewChatClient() => new TestChatClient();

    [Fact]
    public void AddDurableAgent_WithName_RunsConfigureDelegate()
    {
        var options = new TemporalAgentsOptions();
        var invocationCount = 0;
        DurableAgentBuilder? observed = null;

        options.AddDurableAgent("MyAgent", agent =>
        {
            invocationCount++;
            observed = agent;
            agent.ChatClient = _ => NewChatClient();
        });

        Assert.Equal(1, invocationCount);
        Assert.NotNull(observed);
        Assert.Equal("MyAgent", observed!.Name);
    }

    [Fact]
    public void AddDurableAgent_WithEmptyName_ThrowsArgumentException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentException>(() =>
            options.AddDurableAgent(string.Empty, _ => { }));
    }

    [Fact]
    public void AddDurableAgent_WithWhitespaceName_ThrowsArgumentException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentException>(() =>
            options.AddDurableAgent("   ", _ => { }));
    }

    [Fact]
    public void AddDurableAgent_WithNullName_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddDurableAgent(null!, _ => { }));
    }

    [Fact]
    public void AddDurableAgent_WithNullConfigure_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddDurableAgent("X", null!));
    }

    [Fact]
    public void AddDurableAgent_WithoutChatClient_ThrowsInvalidOperationException()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("NoClient", _ => { /* no ChatClient assignment */ }));
        Assert.Contains("NoClient", ex.Message);
        Assert.Contains("ChatClient", ex.Message);
    }

    [Fact]
    public void AddDurableAgent_DuplicateName_ThrowsInvalidOperationException()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Dup", agent => agent.ChatClient = _ => NewChatClient());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("Dup", agent => agent.ChatClient = _ => NewChatClient()));
        Assert.Contains("Dup", ex.Message);
    }

    [Fact]
    public void AddDurableAgent_DuplicatesProxy_ThrowsInvalidOperationException()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentProxy("Shared");

        Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("Shared", agent => agent.ChatClient = _ => NewChatClient()));
    }

    [Fact]
    public void AddDurableAgent_DuplicateNameIsCaseInsensitive()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent => agent.ChatClient = _ => NewChatClient());

        Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("MYAGENT", agent => agent.ChatClient = _ => NewChatClient()));
    }

    [Fact]
    public void AddDurableAgent_RegistrationStoresFlattenedState()
    {
        var options = new TemporalAgentsOptions();
        var chatClient = NewChatClient();
        var chatOptions = new ChatOptions { Temperature = 0.42f };

        options.AddDurableAgent("Stored", agent =>
        {
            agent.Description = "a stored agent";
            agent.Instructions = "be helpful";
            agent.ChatClient = _ => chatClient;
            agent.ChatOptions = chatOptions;
            agent.MaxToolCallsPerTurn = 9;
        });

        var registration = Assert.Single(options.DurableAgentRegistrations);
        Assert.Equal("Stored", registration.Key);
        Assert.Equal("Stored", registration.Value.Name);
        Assert.Equal("a stored agent", registration.Value.Description);
        Assert.Equal("be helpful", registration.Value.Instructions);
        Assert.Same(chatClient, registration.Value.ChatClient(null!));
        Assert.Equal(0.42f, registration.Value.ChatOptions!.Temperature);
        Assert.Equal(9, registration.Value.MaxToolCallsPerTurn);
    }

    [Fact]
    public void AddDurableAgent_GetRegisteredAgentNames_IncludesDurableAgent()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Listed", agent => agent.ChatClient = _ => NewChatClient());

        var names = options.GetRegisteredAgentNames();
        Assert.Contains("Listed", names);
    }

    [Fact]
    public void AddDurableAgent_IsAgentRegistered_ReturnsTrue()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Looked", agent => agent.ChatClient = _ => NewChatClient());

        Assert.True(options.IsAgentRegistered("Looked"));
    }

    [Fact]
    public void AddDurableAgent_GetAgentDescriptors_IncludesDurableAgentWithDescription()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Described", agent =>
        {
            agent.Description = "specialist agent";
            agent.ChatClient = _ => NewChatClient();
        });

        var descriptors = options.GetAgentDescriptors();
        var descriptor = Assert.Single(descriptors);
        Assert.Equal("Described", descriptor.Name);
        Assert.Equal("specialist agent", descriptor.Description);
    }

    [Fact]
    public void AddDurableAgent_DescriptionlessAgent_OmittedFromDescriptors()
    {
        // Mirrors the legacy behavior — an agent with no description is excluded from the
        // routing prompt list. This keeps classifier/utility agents out of dispatch prompts.
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Anonymous", agent => agent.ChatClient = _ => NewChatClient());

        Assert.Empty(options.GetAgentDescriptors());
    }

    [Fact]
    public void AddDurableAgent_ReturnsSameOptionsInstance()
    {
        var options = new TemporalAgentsOptions();
        var returned = options.AddDurableAgent("Chained", agent => agent.ChatClient = _ => NewChatClient());
        Assert.Same(options, returned);
    }

    [Fact]
    public void BuildAgentWorkflowInput_ProxyOnly_UsesWorkerDefaults()
    {
        // Proxy-only: no AddDurableAgent call. Worker-level defaults flow through.
        var options = new TemporalAgentsOptions
        {
            DefaultTimeToLive = TimeSpan.FromHours(6),
            DefaultActivityTimeout = TimeSpan.FromMinutes(7),
            DefaultHeartbeatTimeout = TimeSpan.FromMinutes(3),
            DefaultApprovalTimeout = TimeSpan.FromDays(2),
            DefaultMaxEntryCount = 250,
            EnableSearchAttributes = true,
        };
        options.AddAgentProxy("Foo");

        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("Foo", options, "tq");

        Assert.NotNull(input);
        Assert.Equal("Foo", input.AgentName);
        Assert.Equal("tq", input.TaskQueue);
        Assert.Equal(TimeSpan.FromHours(6), input.TimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(7), input.ActivityTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), input.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromDays(2), input.ApprovalTimeout);
        Assert.Equal(250, input.MaxEntryCount);
        Assert.True(input.EnableSearchAttributes);
        Assert.Null(input.DurableAgentToolActivityOptions);
        Assert.False(input.UseExternalStoreMode);
    }

    [Fact]
    public void BuildAgentWorkflowInput_ProxyOnly_RespectsProxyDeclarationTtl()
    {
        // Per-agent TTL on the proxy declaration wins over the worker default.
        var options = new TemporalAgentsOptions
        {
            DefaultTimeToLive = TimeSpan.FromHours(6),
        };
        options.AddAgentProxy("Foo", timeToLive: TimeSpan.FromHours(2));

        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("Foo", options, "tq");

        Assert.Equal(TimeSpan.FromHours(2), input.TimeToLive);
    }

    [Fact]
    public void BuildAgentWorkflowInput_ProxyOnly_NullDefaultTtl_FallsBackToFourteenDays()
    {
        // When neither the proxy declaration nor the worker default specify a TTL, fall back
        // to the documented 14-day default — same rule as the durable-agent path.
        var options = new TemporalAgentsOptions
        {
            DefaultTimeToLive = null,
        };
        options.AddAgentProxy("Foo");

        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("Foo", options, "tq");

        Assert.Equal(TimeSpan.FromDays(14), input.TimeToLive);
    }

    [Fact]
    public void BuildAgentWorkflowInput_NotRegisteredAtAll_Throws()
    {
        // Neither durable nor proxy registered — surface the misconfiguration clearly.
        var options = new TemporalAgentsOptions();

        Assert.Throws<AgentNotRegisteredException>(() =>
            DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("Missing", options, "tq"));
    }

    // ── Task 8.2: DurableToolOptions.ScopeAware() and invalid combinations ────

    [Fact]
    public void ScopeAware_FluentsSetsScopeAwareFlag()
    {
        var opts = new DurableToolOptions();
        Assert.False(opts.ScopeAwareFlag);

        var returned = opts.ScopeAware();

        Assert.True(opts.ScopeAwareFlag);
        Assert.Same(opts, returned);
    }

    [Fact]
    public void UseApprovalScopes_NoExistingInterceptor_Succeeds()
    {
        var options = new TemporalAgentsOptions();
        // Should not throw.
        options.AddDurableAgent("ScopeAgent", a =>
        {
            a.ChatClient = _ => NewChatClient();
            a.UseApprovalScopes();
        });
    }

    [Fact]
    public void UseApprovalScopes_AfterAddToolInterceptor_ThrowsInvalidOperationException()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("ScopeAgent2", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.AddToolInterceptor(_ => new StubInterceptorForBuilder());
                a.UseApprovalScopes(); // Must throw
            }));
        Assert.Contains("UseApprovalScopes()", ex.Message);
        Assert.Contains("AddToolInterceptor()", ex.Message);
    }

    [Fact]
    public void AddToolInterceptor_AfterUseApprovalScopes_ThrowsInvalidOperationException()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("ScopeAgent3", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.UseApprovalScopes();
                a.AddToolInterceptor(_ => new StubInterceptorForBuilder()); // Must throw
            }));
        Assert.Contains("ScopedApprovalInterceptor", ex.Message);
    }

    [Fact]
    public void UseApprovalScopes_WithDefaultToolInterceptor_ThrowsAtStartupResolution()
    {
        // The DefaultToolInterceptor incompatibility is caught at resolution time, not builder time.
        var options = new TemporalAgentsOptions();
        options.DefaultToolInterceptor = _ => new StubInterceptorForBuilder();
        options.AddDurableAgent("ScopeAgentIncompat", a =>
        {
            a.ChatClient = _ => NewChatClient();
            a.UseApprovalScopes();
        });

        // Exception raised at resolution time: BuildAgentWorkflowInputCore.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("ScopeAgentIncompat", options, "tq"));

        Assert.Contains("UseApprovalScopes()", ex.Message);
        Assert.Contains("DefaultToolInterceptor", ex.Message);
        Assert.Contains("This release does not compose approval scopes", ex.Message);
    }

    [Fact]
    public void NoUseApprovalScopes_WithDefaultToolInterceptor_AgentNotUsingScopes_Unchanged()
    {
        // Agents without UseApprovalScopes() plus a worker-default interceptor keep existing behavior.
        var options = new TemporalAgentsOptions();
        options.DefaultToolInterceptor = _ => new StubInterceptorForBuilder();
        options.AddDurableAgent("NormalAgent", a =>
        {
            a.ChatClient = _ => NewChatClient();
            // No UseApprovalScopes — no conflict
        });

        // Should not throw.
        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("NormalAgent", options, "tq");
        Assert.NotNull(input);
        Assert.False(input.UseApprovalScopes);
    }

    [Fact]
    public void RequireApprovalAndScopeAware_WithoutUseApprovalScopes_ThrowsAtRegistration()
    {
        // RequireApproval + ScopeAware without UseApprovalScopes is caught eagerly at
        // AddDurableAgent → ToRegistration() time (early-failure design).
        var options = new TemporalAgentsOptions();
        var tool = AIFunctionFactory.Create(() => "result", "write_file");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("BadAgent", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.AddTool(tool, opts => opts.RequireApproval().ScopeAware());
            }));

        Assert.Contains("ScopeAware()", ex.Message);
        Assert.Contains("UseApprovalScopes()", ex.Message);
    }

    [Fact]
    public void RequireApprovalScopeAwareSkipInterceptor_ThrowsAtRegistration()
    {
        // RequireApproval + ScopeAware + SkipInterceptor is caught eagerly at
        // AddDurableAgent → ToRegistration() time. UseApprovalScopes must be called first
        // to reach the SkipInterceptor check (ScopeAware+!UseApprovalScopes fires first otherwise).
        var options = new TemporalAgentsOptions();
        var tool = AIFunctionFactory.Create(() => "result", "write_file");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("SkipScopeAgent", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.UseApprovalScopes();
                a.AddTool(tool, opts => opts.RequireApproval().ScopeAware().SkipInterceptor());
            }));

        Assert.Contains("SkipInterceptor()", ex.Message);
        Assert.Contains("RequireApproval()", ex.Message);
        Assert.Contains("ScopeAware()", ex.Message);
    }

    [Fact]
    public void ScopeAwareSkipInterceptor_WithoutRequireApproval_AcceptedAtRegistration()
    {
        // .ScopeAware().SkipInterceptor() without .RequireApproval() is valid.
        var options = new TemporalAgentsOptions();
        var tool = AIFunctionFactory.Create(() => "result", "read_file");
        options.AddDurableAgent("SkipScopeNoApproval", a =>
        {
            a.ChatClient = _ => NewChatClient();
            a.AddTool(tool, opts => opts.ScopeAware().SkipInterceptor());
        });

        // No exception at either builder or resolution time.
        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("SkipScopeNoApproval", options, "tq");
        Assert.NotNull(input);
    }

    [Fact]
    public void AlwaysScopesStoreKey_Whitespace_ThrowsAtToRegistration()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("StoreKeyWhitespace", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.UseApprovalScopes(opts => opts.AlwaysScopesStoreKey = "   ");
            }));
        Assert.Contains("AlwaysScopesStoreKey", ex.Message);
    }

    [Fact]
    public void AlwaysScopesStoreKey_ReservedSessionKey_ThrowsAtToRegistration()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("ReservedKey", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.UseApprovalScopes(opts => opts.AlwaysScopesStoreKey = "temporal.approval_scopes.session");
            }));
        Assert.Contains("temporal.approval_scopes.session", ex.Message);
        Assert.Contains("reserved", ex.Message);
    }

    [Fact]
    public void AlwaysScopesStoreKey_DefaultValue_NoException()
    {
        var options = new TemporalAgentsOptions();
        // Default "temporal.approval_scopes.always" must pass validation.
        options.AddDurableAgent("DefaultKey", a =>
        {
            a.ChatClient = _ => NewChatClient();
            a.UseApprovalScopes();
        });

        // Should not throw.
        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("DefaultKey", options, "tq");
        Assert.NotNull(input);
    }

    [Fact]
    public void MaxAlwaysScopeCacheRecords_Zero_ThrowsAtToRegistration()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("ZeroRecords", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.UseApprovalScopes(opts => opts.MaxAlwaysScopeCacheRecords = 0);
            }));
        Assert.Contains("MaxAlwaysScopeCacheRecords", ex.Message);
    }

    [Fact]
    public void MaxAlwaysScopeCacheBytes_Zero_ThrowsAtToRegistration()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("ZeroBytes", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.UseApprovalScopes(opts => opts.MaxAlwaysScopeCacheBytes = 0);
            }));
        Assert.Contains("MaxAlwaysScopeCacheBytes", ex.Message);
    }

    [Fact]
    public void ApprovalScopeActivityMaximumAttempts_Zero_ThrowsAtToRegistration()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("ZeroAttempts", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.UseApprovalScopes(opts => opts.ApprovalScopeActivityMaximumAttempts = 0);
            }));
        Assert.Contains("ApprovalScopeActivityMaximumAttempts", ex.Message);
    }

    [Fact]
    public void ApprovalScopeActivityTimeout_Zero_ThrowsAtToRegistration()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("ZeroTimeout", a =>
            {
                a.ChatClient = _ => NewChatClient();
                a.UseApprovalScopes(opts => opts.ApprovalScopeActivityTimeout = TimeSpan.Zero);
            }));
        Assert.Contains("ApprovalScopeActivityTimeout", ex.Message);
    }

    // ── Internal stubs ──────────────────────────────────────────────────────

    private sealed class StubInterceptorForBuilder : IAgentToolInterceptor
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            AgentToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableToolDecision.Proceed());
    }

    private sealed class TestChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
