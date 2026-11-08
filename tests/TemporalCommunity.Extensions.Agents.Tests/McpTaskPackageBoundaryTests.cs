using Xunit;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Agents;

namespace TemporalCommunity.Extensions.Agents.Tests;

public sealed class McpTaskPackageBoundaryTests
{
    [Fact]
    public void ProductionAssemblies_DoNotReferenceMcpTasksExtension()
    {
        const string TasksAssemblyName = "ModelContextProtocol.Extensions.Tasks";

        Assert.DoesNotContain(
            typeof(TemporalAgentsOptions).Assembly.GetReferencedAssemblies(),
            assembly => string.Equals(assembly.Name, TasksAssemblyName, StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(DurableAIDataConverter).Assembly.GetReferencedAssemblies(),
            assembly => string.Equals(assembly.Name, TasksAssemblyName, StringComparison.Ordinal));
    }
}
