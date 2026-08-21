using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using TemporalCommunity.Samples.Mcp.WorkflowToolServer;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class WorkflowToolResultMapperTests
{
    [Theory]
    [InlineData("completed", false)]
    [InlineData("conflict", true)]
    [InlineData("failed", true)]
    public void ToCallToolResult_SynchronizesContentAndErrorSemantics(
        string status,
        bool expectedIsError)
    {
        var expected = new WorkflowToolResult(
            "operation-42",
            status,
            status == "completed" ? "processed" : null,
            status == "completed" ? null : $"{status}_code");

        var result = WorkflowOperationTools.ToCallToolResult(expected);

        var structured = result.StructuredContent
            ?? throw new Xunit.Sdk.XunitException("The result did not contain structured content.");
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        Assert.Equal(structured.GetRawText(), text);
        Assert.Equal(
            expected,
            structured.Deserialize<WorkflowToolResult>(McpJsonUtilities.DefaultOptions));
        Assert.Equal(
            expected,
            JsonSerializer.Deserialize<WorkflowToolResult>(text, McpJsonUtilities.DefaultOptions));
        Assert.Equal(expectedIsError, result.IsError);
    }
}
