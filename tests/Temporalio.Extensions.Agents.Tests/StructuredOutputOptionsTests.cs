using System.Text.Json;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class StructuredOutputOptionsTests
{
    [Fact]
    public void DefaultMaxRetries_Is2()
    {
        var options = new StructuredOutputOptions();
        Assert.Equal(2, options.MaxRetries);
    }

    [Fact]
    public void DefaultIncludeErrorContext_IsTrue()
    {
        var options = new StructuredOutputOptions();
        Assert.True(options.IncludeErrorContext);
    }

    [Fact]
    public void DefaultJsonSerializerOptions_IsNull()
    {
        var options = new StructuredOutputOptions();
        Assert.Null(options.JsonSerializerOptions);
    }

    [Fact]
    public void MaxRetries_CanBeCustomized()
    {
        var options = new StructuredOutputOptions { MaxRetries = 5 };
        Assert.Equal(5, options.MaxRetries);
    }

    [Fact]
    public void IncludeErrorContext_CanBeDisabled()
    {
        var options = new StructuredOutputOptions { IncludeErrorContext = false };
        Assert.False(options.IncludeErrorContext);
    }

    [Fact]
    public void JsonSerializerOptions_CanBeSet()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var options = new StructuredOutputOptions { JsonSerializerOptions = jsonOptions };
        Assert.Same(jsonOptions, options.JsonSerializerOptions);
    }
}
