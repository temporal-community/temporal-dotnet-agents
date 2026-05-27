using System.Linq;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Verifies the round-trip behavior of <see cref="DurableChatInput"/> through
/// <see cref="DurableAIDataConverter"/> when <see cref="ChatOptions.Tools"/> is populated.
///
/// Context (DurableChat review): <c>DurableChatSessionClient</c> stuffs the caller-supplied
/// <see cref="ChatOptions"/> (including its <see cref="ChatOptions.Tools"/> list of
/// <see cref="AIFunction"/> references) into <see cref="DurableChatInput"/>, which is the
/// update payload sent over the wire and persisted in workflow history. <see cref="AIFunction"/>
/// is not a value type — tools are typically reconstituted activity-side via the
/// <c>DurableFunctionRegistry</c> rather than by serializing the function body itself.
/// What survives the converter is what matters: if <c>Tools</c> silently collapses to
/// <c>null</c> or empty on the deserialize side, the "explicit subset of tools" mental model
/// (samples/MEAI/DurableTools Scenario 1) is misleading — every call would behave as if
/// no <c>Tools</c> were supplied.
/// </summary>
public class DurableChatInputSerializationTests
{
    [Fact]
    public void DurableChatInput_RoundTrips_With_ChatOptions_Tools_Populated()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        // A trivial AIFunction. Body content shouldn't survive — what we care about is the
        // shape of the Tools list and the Name discriminator on each entry post-deserialize.
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

        // Sanity precondition: the source input has exactly one tool named "get_weather".
        Assert.NotNull(input.Options);
        Assert.NotNull(input.Options!.Tools);
        Assert.Single(input.Options.Tools!);
        Assert.Equal("get_weather", input.Options.Tools![0].Name);

        var payload = converter.ToPayload(input);
        var deserialized = (DurableChatInput)converter.ToValue(
            payload, typeof(DurableChatInput))!;

        // Scalars from ChatOptions are expected to survive — established by
        // SerializationTests.ChatOptions_SerializableFields_Preserved. These assertions
        // pin that baseline so a Tools-only regression is easy to diagnose.
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Options);
        Assert.Equal("gpt-4o", deserialized.Options!.ModelId);
        Assert.Equal(0.2f, deserialized.Options.Temperature);

        // The load-bearing assertions. If any of these fail, the sample's "Tools" subset
        // story is fiction and activity-side reconstitution comes solely from the registry.
        //
        // VERIFIED BUG (2026-05-26): deserialized.Options.Tools is NULL after round-trip
        // through DurableAIDataConverter.Instance.PayloadConverter. ChatOptions scalars
        // (ModelId, Temperature) survive. The Tools list silently collapses to null.
        // Root cause is most likely AIJsonUtilities.DefaultOptions / source-gen treating
        // AITool as polymorphic without a registered discriminator for AIFunction.
        // Impact: samples/MEAI/DurableTools Scenario 1 ("explicit subset via ChatOptions.Tools")
        // is misleading — the activity always falls back to the DurableFunctionRegistry.
        Assert.NotNull(deserialized.Options.Tools);
        Assert.Single(deserialized.Options.Tools!);
        Assert.Equal("get_weather", deserialized.Options.Tools![0].Name);
    }

    [Fact]
    public void DurableChatInput_RoundTrips_With_Multiple_Tools_Preserves_Order()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var toolA = AIFunctionFactory.Create((string s) => s, name: "echo_a");
        var toolB = AIFunctionFactory.Create((int n) => n + 1, name: "inc_b");
        var toolC = AIFunctionFactory.Create(() => "ok", name: "ping_c");

        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, "test multi"),
            },
            Options = new ChatOptions
            {
                Tools = new List<AITool> { toolA, toolB, toolC },
            },
        };

        var payload = converter.ToPayload(input);
        var deserialized = (DurableChatInput)converter.ToValue(
            payload, typeof(DurableChatInput))!;

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.Tools);
        Assert.Equal(3, deserialized.Options.Tools!.Count);
        Assert.Equal("echo_a", deserialized.Options.Tools![0].Name);
        Assert.Equal("inc_b", deserialized.Options.Tools![1].Name);
        Assert.Equal("ping_c", deserialized.Options.Tools![2].Name);
    }

    [Fact]
    public void DurableChatInput_RoundTrips_Preserves_Tools_Order()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        // Deliberately non-alphabetical, non-insertion-sorted names so the test catches a
        // converter that silently sorts tools by name (alphabetical input would pass even
        // under a sorting bug). The original order — zeta, alpha, mike, bravo — must come
        // back identically; otherwise per-call tool-selection behavior shifts in ways the
        // caller cannot reason about.
        var zetaTool = AIFunctionFactory.Create((string s) => s, name: "zeta_tool");
        var alphaTool = AIFunctionFactory.Create((string s) => s, name: "alpha_tool");
        var mikeTool = AIFunctionFactory.Create((string s) => s, name: "mike_tool");
        var bravoTool = AIFunctionFactory.Create((string s) => s, name: "bravo_tool");

        var originalNames = new[] { "zeta_tool", "alpha_tool", "mike_tool", "bravo_tool" };

        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, "ordering check"),
            },
            Options = new ChatOptions
            {
                Tools = new List<AITool> { zetaTool, alphaTool, mikeTool, bravoTool },
            },
        };

        var payload = converter.ToPayload(input);
        var deserialized = (DurableChatInput)converter.ToValue(
            payload, typeof(DurableChatInput))!;

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.Tools);

        var deserializedNames = deserialized.Options.Tools!.Select(t => t.Name).ToArray();
        Assert.Equal(originalNames, deserializedNames);
    }
}
