using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public class AgentRunToolSelectionPolicyTests
{
    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name);

    public static TheoryData<bool, IReadOnlyList<string>?, string[]> ProviderSelectionCases => new()
    {
        { false, null, [] },
        { false, ["alpha"], [] },
        { true, null, ["alpha", "beta"] },
        { true, [], [] },
        { true, ["alpha"], ["alpha"] },
        { true, ["unknown"], [] },
        { true, ["unknown", "beta"], ["beta"] },
        { true, ["ALPHA", "alpha"], ["alpha"] },
    };

    [Theory]
    [MemberData(nameof(ProviderSelectionCases))]
    public void FilterProviderTools_AppliesCanonicalSelectionMatrix(
        bool enabled,
        IReadOnlyList<string>? enabledNames,
        string[] expectedNames)
    {
        IReadOnlyList<AITool> registered = [Tool("alpha"), Tool("beta")];

        var result = AgentRunToolSelectionPolicy.FilterProviderTools(
            registered, enabled, enabledNames);

        Assert.Equal(expectedNames, result.Select(tool => tool.Name));
        Assert.Equal(["alpha", "beta"], registered.Select(tool => tool.Name));
    }

    [Fact]
    public void FilterProviderTools_DeduplicatesRegisteredNamesInRegisteredOrder()
    {
        IReadOnlyList<AITool> registered = [Tool("alpha"), Tool("ALPHA"), Tool("beta")];

        var result = AgentRunToolSelectionPolicy.FilterProviderTools(
            registered, enableToolCalls: true, enabledNames: null);

        Assert.Equal(["alpha", "beta"], result.Select(tool => tool.Name));
    }

    public static TheoryData<string?, bool, IReadOnlyList<string>?, bool> DispatchCases => new()
    {
        { "alpha", false, null, false },
        { "alpha", true, null, true },
        { "ALPHA", true, null, true },
        { "alpha", true, [], false },
        { "alpha", true, ["beta"], false },
        { "alpha", true, ["ALPHA"], true },
        { "unknown", true, null, false },
        { null, true, null, false },
        { "", true, null, false },
        { "   ", true, null, false },
    };

    [Theory]
    [MemberData(nameof(DispatchCases))]
    public void IsCallEnabled_AppliesCanonicalDispatchMatrix(
        string? name,
        bool enabled,
        IReadOnlyList<string>? enabledNames,
        bool expected)
    {
        IReadOnlyCollection<string> registered = ["alpha", "beta"];

        var result = AgentRunToolSelectionPolicy.IsCallEnabled(
            name, registered, enabled, enabledNames);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("known")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void CreateBlockedResult_DoesNotDiscloseNameOrRegistry(string? name)
    {
        var result = AgentRunToolSelectionPolicy.CreateBlockedResult(name);

        Assert.Equal(AgentRunToolSelectionPolicy.BlockedResult, result);
        Assert.DoesNotContain("known", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown", result, StringComparison.OrdinalIgnoreCase);
    }
}
