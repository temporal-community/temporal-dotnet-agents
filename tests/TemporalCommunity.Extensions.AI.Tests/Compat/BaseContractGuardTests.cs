using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Compat;

/// <summary>
/// Base-contract guard suite for <c>TemporalCommunity.Extensions.AI</c> (plan §2.2 / §2.4,
/// findings F-2a / F-3 / F-5).
/// </summary>
/// <remarks>
/// <para>
/// These are fast, no-server CI canaries. Their job is to turn a <i>silent</i> upstream
/// Microsoft.Extensions.AI (MEAI) base-library bump into a <i>red</i> unit test. Each guard
/// pins exactly one contract that our production code reflects on, string-matches, or
/// serializes — contracts the compiler cannot protect because they are <c>protected</c>
/// members, polymorphism tables, or the <i>absence</i> of polymorphism.
/// </para>
/// <para>
/// <b>Single-source rule.</b> Where a production constant or production serializer expresses
/// the contract, these tests assert that the <i>production</i> artifact resolves — they never
/// re-declare the FQN, member name, or wire format. A rename on either side then shows up here.
/// </para>
/// <para>
/// <b>Superset, not equality.</b> The discriminator canary asserts the live base table is a
/// <i>superset</i> of the discriminators our history serializes. Our own additions and new
/// upstream content types never false-positive; only a removal or rename of a discriminator we
/// depend on fails the test.
/// </para>
/// </remarks>
public class BaseContractGuardTests
{
    // -----------------------------------------------------------------------------------------
    // S-F-2a (MEAI) — DelegatingChatClient.InnerClient still resolves.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Pins the protected <c>DelegatingChatClient.InnerClient</c> property that
    /// <see cref="TemporalCommunity.Extensions.AI.Internal.AgentChainWalker"/> reflects by string name
    /// (AgentChainWalker.cs:68-71) to walk the <see cref="IChatClient"/> decorator chain.
    /// </summary>
    /// <remarks>
    /// If MEAI renames or removes this protected member, the chain-walk primary in
    /// <c>FindFirst&lt;T&gt;</c> silently degrades to the <c>GetService&lt;T&gt;()</c> fallback —
    /// FICC-style detection and OTel suppression detection quietly stop seeing inner links. This
    /// test forces that drift to fail at CI time. We reflect with the SAME binding flags the
    /// production walker uses so the test fails for exactly the reason the walker would break.
    /// </remarks>
    [Fact]
    public void Meai_DelegatingChatClient_InnerClient_StillResolves()
    {
        var prop = typeof(DelegatingChatClient).GetProperty(
            "InnerClient",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(prop);
        Assert.True(
            typeof(IChatClient).IsAssignableFrom(prop!.PropertyType),
            $"DelegatingChatClient.InnerClient resolved but its type '{prop.PropertyType}' is no " +
            "longer assignable to IChatClient — AgentChainWalker.WalkChatClient would break.");
    }

    // -----------------------------------------------------------------------------------------
    // S-F-3 — AIContent $type discriminator superset canary.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts the live <see cref="AIContent"/> <c>[JsonDerivedType]</c> table (as seen through
    /// <see cref="AIJsonUtilities.DefaultOptions"/>, the base our durable converter is built on)
    /// is a <b>superset</b> of the discriminators our durable history actually serializes,
    /// captured in <c>Snapshots/meai-base/discriminators.json</c>.
    /// </summary>
    /// <remarks>
    /// The snapshot lists only <c>text</c> / <c>functionCall</c> / <c>functionResult</c> /
    /// <c>usage</c> — the four AIContent subtypes that flow through ChatMessage/ChatResponse in
    /// our durable history (see DurableAIDataConverterTests). Subset (not equality) means our own
    /// additions and unrelated new upstream content types never false-positive; only a base
    /// <i>removal or rename</i> of one of these discriminators makes an old Temporal history
    /// un-replayable, and that is exactly what fails here. The failure message names the missing
    /// discriminator.
    /// </remarks>
    [Fact]
    public void Meai_AIContent_DiscriminatorTable_IsSupersetOfSnapshot()
    {
        var expected = LoadSnapshotDiscriminators(
            "meai-base", "Microsoft.Extensions.AI.AIContent");

        var live = LiveAIContentDiscriminators();

        var missing = expected.Where(d => !live.Contains(d)).ToList();

        Assert.True(
            missing.Count == 0,
            $"Base AIContent [JsonDerivedType] table is missing discriminator(s) our durable " +
            $"history serializes: [{string.Join(", ", missing)}]. " +
            $"Live discriminators: [{string.Join(", ", live.OrderBy(x => x))}]. " +
            "An MEAI bump renamed or removed an AIContent $type we depend on — old Temporal " +
            "histories using it will no longer replay. Update production serialization and the " +
            "meai-base snapshot together.");
    }

    // -----------------------------------------------------------------------------------------
    // S-F-3 — Golden-payload replay through the production converter.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A frozen, hand-verified JSON payload — a single assistant <see cref="ChatMessage"/>
    /// carrying <see cref="TextContent"/> + <see cref="FunctionCallContent"/> +
    /// <see cref="FunctionResultContent"/> — round-tripped through
    /// <see cref="DurableAIJsonUtilities.DefaultOptions"/> (the exact options our durable history
    /// uses). Asserts the concrete content types and their key fields survive.
    /// </summary>
    /// <remarks>
    /// This catches a discriminator rename OR a property rename inside any of the three content
    /// types — either would change how a previously-persisted history deserializes. The payload
    /// is literal (not generated from the live types) so it represents bytes a prior worker would
    /// actually have written to Temporal history. Per finding X-6, FunctionResultContent.Result
    /// surfaces as a <see cref="JsonElement"/> across the durable boundary by design; we assert
    /// its value through that boundary rather than expecting a rehydrated domain type.
    /// </remarks>
    [Fact]
    public void Meai_GoldenPayload_RoundTripsThroughDurableOptions()
    {
        // Frozen wire bytes: an assistant message with text, a function call, and a function
        // result. $type discriminators and property names are MEAI-canonical for 10.5.0.
        const string GoldenPayload =
            """
            {
              "authorName": null,
              "role": "assistant",
              "contents": [
                { "$type": "text", "text": "Booking your flight now." },
                {
                  "$type": "functionCall",
                  "callId": "call-42",
                  "name": "book_flight",
                  "arguments": { "destination": "SEA", "passengers": 2 }
                },
                {
                  "$type": "functionResult",
                  "callId": "call-42",
                  "result": { "confirmation": "ABC123" }
                }
              ]
            }
            """;

        var message = JsonSerializer.Deserialize<ChatMessage>(
            GoldenPayload, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(message);
        Assert.Equal(ChatRole.Assistant, message!.Role);
        Assert.Equal(3, message.Contents.Count);

        // 1) TextContent — discriminator "text", property "text".
        var text = Assert.IsType<TextContent>(message.Contents[0]);
        Assert.Equal("Booking your flight now.", text.Text);

        // 2) FunctionCallContent — discriminator "functionCall"; CallId / Name / Arguments.
        var call = Assert.IsType<FunctionCallContent>(message.Contents[1]);
        Assert.Equal("call-42", call.CallId);
        Assert.Equal("book_flight", call.Name);
        Assert.NotNull(call.Arguments);
        Assert.True(call.Arguments!.ContainsKey("destination"));

        // 3) FunctionResultContent — discriminator "functionResult"; CallId survives.
        //    Result crosses the durable boundary as a JsonElement by design (finding X-6).
        var result = Assert.IsType<FunctionResultContent>(message.Contents[2]);
        Assert.Equal("call-42", result.CallId);
        var resultElement = Assert.IsType<JsonElement>(result.Result);
        Assert.Equal(
            "ABC123", resultElement.GetProperty("confirmation").GetString());
    }

    // -----------------------------------------------------------------------------------------
    // S-F-5 — AITool polymorphism watch.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Round-trips a <see cref="ChatOptions"/> whose <see cref="ChatOptions.Tools"/> list is
    /// populated, through the production durable options
    /// (<see cref="DurableAIJsonUtilities.DefaultOptions"/>, which installs
    /// <c>ChatOptionsToolsJsonConverter</c> and its <c>$toolNames</c> sidecar). Asserts the
    /// current name-only behavior holds: tool names survive, with no double-encode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is the canary for MEAI adding AITool polymorphism.</b>
    /// <c>ChatOptionsToolsJsonConverter</c> exists precisely because <see cref="AITool"/> has NO
    /// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> in the base library — verified in the MEAI
    /// source at <c>Microsoft.Extensions.AI.Abstractions/Tools/AITool.cs</c> (the type is declared
    /// <c>public abstract class AITool</c> with no polymorphism attributes). If a future MEAI bump
    /// ADDS polymorphism to AITool, the base serializer would start emitting a <c>$type</c>-tagged
    /// Tools array, and our sidecar converter would then double-encode (sidecar + base array). The
    /// assertions below — exactly one tool, name preserved, no per-tool <c>$type</c> leakage —
    /// would break, surfacing that bump at CI time instead of in a customer's history.
    /// </para>
    /// </remarks>
    [Fact]
    public void Meai_ChatOptionsTools_RoundTrip_NoDoubleEncode()
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(
                () => "ok", name: "lookup_order")],
        };

        var json = JsonSerializer.Serialize(options, DurableAIJsonUtilities.DefaultOptions);

        // The production wire format carries tool NAMES in a $toolNames sidecar, not a
        // polymorphic per-tool $type array. If MEAI adds AITool polymorphism, the base
        // serializer would emit the Tools array itself and this expectation would change.
        Assert.Contains("$toolNames", json);
        Assert.Contains("lookup_order", json);

        var roundTripped = JsonSerializer.Deserialize<ChatOptions>(
            json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped!.Tools);
        // Exactly one tool — a double-encode (sidecar + base polymorphic array) would change
        // the count or materialize duplicate/typed entries.
        Assert.Single(roundTripped.Tools!);
        Assert.Equal("lookup_order", roundTripped.Tools![0].Name);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the discriminator allow-list for <paramref name="baseTypeFullName"/> out of
    /// <c>Compat/Snapshots/{snapshotName}/discriminators.json</c> (copied next to the test
    /// assembly via the csproj snapshot glob).
    /// </summary>
    private static HashSet<string> LoadSnapshotDiscriminators(
        string snapshotName, string baseTypeFullName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Compat", "Snapshots", snapshotName, "discriminators.json");
        Assert.True(
            File.Exists(path),
            $"Snapshot not found: {path}. Ensure Compat/Snapshots/**/*.json has " +
            "CopyToOutputDirectory=PreserveNewest in the test csproj.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = doc.RootElement
            .GetProperty("baseTypes")
            .GetProperty(baseTypeFullName)
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(list);
        return list;
    }

    /// <summary>
    /// Enumerates the live <see cref="AIContent"/> <c>$type</c> discriminators registered in the
    /// base library, as seen through <see cref="AIJsonUtilities.DefaultOptions"/> — the same base
    /// options <see cref="DurableAIJsonUtilities.DefaultOptions"/> is built on.
    /// </summary>
    private static HashSet<string> LiveAIContentDiscriminators()
    {
        var typeInfo = AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AIContent));
        Assert.NotNull(typeInfo.PolymorphismOptions);

        return typeInfo.PolymorphismOptions!.DerivedTypes
            .Select(d => d.TypeDiscriminator as string)
            .Where(d => d is not null)
            .Select(d => d!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
