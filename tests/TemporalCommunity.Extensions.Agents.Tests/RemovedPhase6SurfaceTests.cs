using System.Reflection;
using TemporalCommunity.Extensions.Agents.Tools;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Guards the intentionally removed scheduling, skill, and reusable-approval APIs without
/// introducing compile-time references to the deleted types or members.
/// </summary>
public sealed class RemovedPhase6SurfaceTests
{
    private static readonly Assembly AgentsAssembly = typeof(TemporalAgentsOptions).Assembly;

    [Fact]
    public void RemovedJobAndSkillTypes_AreAbsent()
    {
        var types = AgentsAssembly.GetTypes();

        Assert.DoesNotContain(types, type => type.Name is "AgentJobInput" or "AgentJobWorkflow");
        Assert.DoesNotContain(types, type =>
            type.Namespace?.StartsWith("TemporalCommunity.Extensions.Agents.Skills", StringComparison.Ordinal)
            == true);
        Assert.DoesNotContain(types, type =>
            type.Namespace?.StartsWith("TemporalCommunity.Extensions.Agents.Approvals", StringComparison.Ordinal)
            == true);
    }

    [Fact]
    public void RemovedSchedulingAndScopeOperations_AreNotPublic()
    {
        AssertNoPublicMethod(typeof(ITemporalAgentClient),
            "RunAgentDelayedAsync", "ScheduleAgentAsync", "GetAgentScheduleHandle");
        AssertNoPublicMethod(typeof(TemporalAIAgentProxy), "RunDelayedAsync");
        AssertNoPublicMethod(typeof(TemporalAgentsOptions), "AddScheduledAgentRun", "GetScheduledRuns");
        AssertNoPublicMethod(typeof(DurableAgentBuilder), "UseSkills", "UseApprovalScopes");
        AssertNoPublicMethod(typeof(DurableToolOptions), "ScopeAware");
        Assert.DoesNotContain(
            typeof(DurableToolOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name == "ScopeAwareFlag");
    }

    [Fact]
    public void ApprovalScopeServiceRegistration_IsAbsent()
    {
        var hasRegistration = AgentsAssembly.GetTypes()
            .Where(type => type.IsAbstract && type.IsSealed)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Any(method => method.Name == "AddTemporalAgentApprovalScopeAdministration");

        Assert.False(hasRegistration);
    }

    private static void AssertNoPublicMethod(Type type, params string[] methodNames)
    {
        var exposedNames = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var methodName in methodNames)
        {
            Assert.DoesNotContain(methodName, exposedNames);
        }
    }
}
