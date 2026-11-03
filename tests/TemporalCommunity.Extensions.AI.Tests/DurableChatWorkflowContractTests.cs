using System.Reflection;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableChatWorkflowContractTests
{
    [Fact]
    public void SessionWorkflow_DeclaresNoSignalHandlers()
    {
        var signalHandlers = typeof(DurableChatWorkflow)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttribute<WorkflowSignalAttribute>() is not null)
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(signalHandlers);
    }
}
