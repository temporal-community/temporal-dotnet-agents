using System.Linq;
using Microsoft.Extensions.AI;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Verifies the durable wire format does not serialize caller-owned tool delegates.
/// Durable sessions reject <see cref="ChatOptions.Tools"/> at their public boundary and build
/// their model schemas from the worker's <c>DurableFunctionRegistry</c>.
/// </summary>
public class DurableChatInputSerializationTests
{
    [Fact]
    public void DurableChatInput_RoundTrips_DropsCallerSuppliedTools()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var weatherTool = AIFunctionFactory.Create(
            (string city) => $"sunny in {city}",
            name: "get_weather",
            description: "Returns the weather for a given city.");

        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, "What is the weather in Seattle?"),
            },
            Options = new ChatOptions
            {
                ModelId = "gpt-4o",
                Temperature = 0.2f,
                Tools = new List<AITool> { weatherTool },
            },
            ConversationId = "conv-tools-roundtrip",
            TurnNumber = 1,
        };

        Assert.NotNull(input.Options);
        Assert.NotNull(input.Options!.Tools);
        Assert.Single(input.Options.Tools!);
        Assert.Equal("get_weather", input.Options.Tools![0].Name);

        var payload = converter.ToPayload(input);
        var deserialized = (DurableChatInput)converter.ToValue(
            payload, typeof(DurableChatInput))!;

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Options);
        Assert.Equal("gpt-4o", deserialized.Options!.ModelId);
        Assert.Equal(0.2f, deserialized.Options.Temperature);

        Assert.Null(deserialized.Options.Tools);
    }
}
