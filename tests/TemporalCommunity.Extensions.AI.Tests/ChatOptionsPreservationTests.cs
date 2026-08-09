using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// S-X-4 regression coverage: <see cref="ChatOptions"/> properties that materially steer the
/// model must survive the durable boundary. Two layers are pinned here:
/// <list type="number">
///   <item>
///     <description>
///       The clone in <c>ChatOptionsSanitizer.PrepareForDurableTransport</c> must copy the
///       four properties Wave 1 added (<see cref="ChatOptions.Instructions"/>,
///       <see cref="ChatOptions.Reasoning"/>, <c>AllowMultipleToolCalls</c>,
///       <c>AllowBackgroundResponses</c>) AND those must survive a
///       <see cref="DurableAIDataConverter"/> round-trip so they reach the activity worker.
///     </description>
///   </item>
///   <item>
///     <description>
///       A reflection guard asserts every <i>settable</i> public property on MEAI's
///       <see cref="ChatOptions"/> is accounted for — either copied by transport preparation
///       (the allow-list) or in the documented deny-list. A future MEAI-added property
///       then fails CI instead of being silently dropped across the durable boundary.
///     </description>
///   </item>
/// </list>
/// </summary>
public class ChatOptionsPreservationTests
{
    /// <summary>
    /// Properties intentionally NOT copied by <c>PrepareForDurableTransport</c>.
    /// <list type="bullet">
    ///   <item><description><c>RawRepresentationFactory</c> — a delegate; not serializable.</description></item>
    ///   <item><description><c>ContinuationToken</c> — provider-specific opaque token, not meaningful to replay.</description></item>
    /// </list>
    /// </summary>
    private static readonly string[] DenyList =
    {
        nameof(ChatOptions.RawRepresentationFactory),
        nameof(ChatOptions.ContinuationToken),
    };

    [Fact]
    public void PrepareForDurableTransport_PreservesTemporalRoutingMetadata()
    {
        var original = new ChatOptions
        {
            ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 }),
            RawRepresentationFactory = _ => null,
        }
            .WithChatClientFactoryKey(string.Empty)
            .WithChatClientTag("tenant", "acme")
            .WithChatClientTag("request", "req-1");
        original.AdditionalProperties!["user.custom"] = "keep";

        var prepared = ChatOptionsSanitizer.PrepareForDurableTransport(original);

        Assert.NotNull(prepared);
        Assert.Equal(string.Empty, prepared!.GetChatClientFactoryKey());
        Assert.Equal(2, prepared.GetChatClientTags().Count);
        Assert.Equal("keep", prepared.AdditionalProperties!["user.custom"]);
        Assert.Null(prepared.ContinuationToken);
        Assert.Null(prepared.RawRepresentationFactory);
        Assert.NotSame(original, prepared);
        Assert.NotSame(original.AdditionalProperties, prepared.AdditionalProperties);

        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var payload = converter.ToPayload(new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            Options = prepared,
            ConversationId = "transport",
        });
        var roundTripped = (DurableChatInput)converter.ToValue(payload, typeof(DurableChatInput))!;
        var roundTrippedOptions = Assert.IsType<ChatOptions>(roundTripped.Options);

        Assert.Equal(string.Empty, roundTrippedOptions.GetChatClientFactoryKey());
        Assert.Equal(2, roundTrippedOptions.GetChatClientTags().Count);
        Assert.Equal("keep", roundTrippedOptions.AdditionalProperties!["user.custom"]?.ToString());
    }

    [Fact]
    public void PrepareForDurableTransport_AndConverterRoundTrip_PreserveSteeringProperties()
    {
        // Set the four properties Wave 1 added plus a couple of established scalars as anchors.
#pragma warning disable MEAI001 // AllowBackgroundResponses is experimental on the pinned MEAI version.
        var original = new ChatOptions
        {
            ModelId = "gpt-4o",
            Temperature = 0.3f,
            Instructions = "Answer in one sentence.",
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            AllowMultipleToolCalls = true,
            AllowBackgroundResponses = false,
        };
#pragma warning restore MEAI001

        // Layer 1: exercise the shipping internal transport helper directly.
        var stripped = ChatOptionsSanitizer.PrepareForDurableTransport(original);

        Assert.NotNull(stripped);
        Assert.Equal("Answer in one sentence.", stripped!.Instructions);
        Assert.Equal(ReasoningEffort.High, stripped.Reasoning?.Effort);
        Assert.True(stripped.AllowMultipleToolCalls);
#pragma warning disable MEAI001
        Assert.False(stripped.AllowBackgroundResponses);
#pragma warning restore MEAI001

        // Layer 2: the stripped options must reach the activity worker intact — i.e. survive a
        // DurableAIDataConverter round-trip as carried on DurableChatInput.Options.
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = stripped,
            ConversationId = "conv-x4",
        };

        var payload = converter.ToPayload(input);
        var roundTripped = (DurableChatInput)converter.ToValue(payload, typeof(DurableChatInput))!;

        Assert.NotNull(roundTripped.Options);
        var opts = roundTripped.Options!;
        Assert.Equal("Answer in one sentence.", opts.Instructions);
        Assert.Equal(ReasoningEffort.High, opts.Reasoning?.Effort);
        Assert.True(opts.AllowMultipleToolCalls);
#pragma warning disable MEAI001
        Assert.False(opts.AllowBackgroundResponses);
#pragma warning restore MEAI001
        // Anchors — confirm the established scalars also still survive.
        Assert.Equal("gpt-4o", opts.ModelId);
        Assert.Equal(0.3f, opts.Temperature);
    }

    [Fact]
    public void PrepareForDurableTransport_CopiesEverySettableProperty_ExceptDocumentedDenyList()
    {
        // Durable canary for X-4: enumerate every settable public property on ChatOptions and
        // assert each is either copied by transport preparation or in the deny-list.
        // If MEAI adds a new settable ChatOptions property in a future bump, this fails CI —
        // forcing a conscious decision (copy it, or document why it is dropped) instead of a
        // silent loss across the durable boundary.

        // Populate every settable property with a distinguishable non-default value, run the
        // real clone, then assert the value either came across (allow) or did not (deny).
        var probe = BuildFullyPopulatedOptions();
        var stripped = ChatOptionsSanitizer.PrepareForDurableTransport(probe);
        Assert.NotNull(stripped);

        var settable = typeof(ChatOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetSetMethod(nonPublic: false) is not null)
            .ToArray();

        // Sanity: the deny-list names must be real settable properties (catches a rename).
        foreach (var denied in DenyList)
        {
            Assert.Contains(settable, p => p.Name == denied);
        }

        foreach (var prop in settable)
        {
            var originalValue = prop.GetValue(probe);
            var strippedValue = prop.GetValue(stripped);

            if (prop.Name == nameof(ChatOptions.AdditionalProperties))
            {
                // AdditionalProperties is cloned so the caller is not mutated. Both user-owned
                // values and serializable Temporal routing metadata belong in durable transport.
                var copied = Assert.IsAssignableFrom<AdditionalPropertiesDictionary>(strippedValue);
                Assert.True(copied.ContainsKey("k"), "User AdditionalProperties key was dropped by transport preparation.");
                Assert.Equal("v", copied["k"]);
                Assert.NotSame(originalValue, strippedValue);
                continue;
            }

            if (DenyList.Contains(prop.Name))
            {
                // Deny-listed: the clone must NOT carry it forward (stays null/default).
                Assert.True(
                    strippedValue is null,
                    $"ChatOptions.{prop.Name} is on the deny-list but transport preparation copied it. " +
                    "Either it is now serializable (move it to the allow-list) or the deny intent is broken.");
            }
            else
            {
                // Allow-listed (everything else): the clone must carry it forward.
                Assert.True(
                    AreEquivalent(originalValue, strippedValue),
                    $"ChatOptions.{prop.Name} is settable and not on the deny-list, but the " +
                    "transport clone did not copy it. A new MEAI property would be silently dropped " +
                    "across the durable boundary — handle it or add it to the documented deny-list.");
            }
        }
    }

    /// <summary>
    /// Populate every settable property with a non-default value so the guard can tell "copied"
    /// from "dropped". Values need only be non-null and recognizable; serializability is checked
    /// by the round-trip test, not here.
    /// </summary>
    private static ChatOptions BuildFullyPopulatedOptions()
    {
        var echoTool = AIFunctionFactory.Create((string s) => s, name: "echo");
#pragma warning disable MEAI001
        return new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["k"] = "v" },
            AllowBackgroundResponses = true,
            AllowMultipleToolCalls = true,
            ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 }),
            ConversationId = "conv-1",
            FrequencyPenalty = 0.1f,
            Instructions = "instr",
            MaxOutputTokens = 256,
            ModelId = "model-x",
            PresencePenalty = 0.2f,
            RawRepresentationFactory = _ => null, // deny-listed delegate.
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
            ResponseFormat = ChatResponseFormat.Text,
            Seed = 7,
            StopSequences = new List<string> { "STOP" },
            Temperature = 0.5f,
            ToolMode = ChatToolMode.Auto,
            Tools = new List<AITool> { echoTool },
            TopK = 3,
            TopP = 0.8f,
        };
#pragma warning restore MEAI001
    }

    private static bool AreEquivalent(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return ReferenceEquals(a, b);
        }

        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is IEnumerable<string> stringsA && b is IEnumerable<string> stringsB)
        {
            return stringsA.SequenceEqual(stringsB);
        }

        if (a is IEnumerable<AITool> toolsA && b is IEnumerable<AITool> toolsB)
        {
            return toolsA.SequenceEqual(toolsB);
        }

        if (a is ReasoningOptions reasoningA && b is ReasoningOptions reasoningB)
        {
            return typeof(ReasoningOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .All(property => Equals(property.GetValue(reasoningA), property.GetValue(reasoningB)));
        }

        return a.Equals(b);
    }
}
