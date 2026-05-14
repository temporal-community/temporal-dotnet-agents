using Temporalio.Extensions.AI.Exceptions;
using Xunit;

namespace Temporalio.Extensions.AI.Tests.Exceptions;

public class DurableFunctionInvocationConflictExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_SetsMessageAndOffendingType()
    {
        var ex = new DurableFunctionInvocationConflictException("conflict detected")
        {
            OffendingType = "Microsoft.Agents.AI.FunctionInvocationDelegatingAgent",
        };

        Assert.Equal("conflict detected", ex.Message);
        Assert.Equal(
            "Microsoft.Agents.AI.FunctionInvocationDelegatingAgent",
            ex.OffendingType);
    }

    [Fact]
    public void Constructor_WithInnerException_PreservesInner()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = new DurableFunctionInvocationConflictException("conflict", inner)
        {
            OffendingType = "Microsoft.Extensions.AI.FunctionInvokingChatClient",
        };

        Assert.Same(inner, ex.InnerException);
        Assert.Equal("Microsoft.Extensions.AI.FunctionInvokingChatClient", ex.OffendingType);
    }

    [Fact]
    public void IsSubtypeOfDurableConfigurationException()
    {
        var ex = new DurableFunctionInvocationConflictException("x")
        {
            OffendingType = "anything",
        };

        Assert.IsAssignableFrom<DurableConfigurationException>(ex);
    }
}
