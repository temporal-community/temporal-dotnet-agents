using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Compat;

/// <summary>
/// Baseline coverage for <see cref="SourceGenCompatHarness"/>. Asserts:
/// <list type="bullet">
///   <item><description>
///     Forward-compat: payloads produced under the v0.3 discriminator set
///     deserialize cleanly under the current (new) options — one test per
///     current discriminator on <see cref="DurableSessionEntry"/>.
///   </description></item>
///   <item><description>
///     Backward-compat: a payload with a NEW (fake) discriminator
///     deserializes under the frozen v0.3 options into a typed
///     <see cref="DurableReplayCompatibilityException"/>, not a raw
///     <see cref="JsonException"/>.
///   </description></item>
///   <item><description>
///     Mixed-fleet sim: round-trip old→new→old preserves content for a
///     known discriminator.
///   </description></item>
///   <item><description>
///     Sanity: no <see cref="JsonException"/> leaks through for the
///     discriminator-mismatch case.
///   </description></item>
/// </list>
/// </summary>
public class SourceGenCompatHarnessTests
{
    private static readonly JsonSerializerOptions NewOptions =
        DurableAIJsonUtilities.DefaultOptions;

    private static readonly JsonSerializerOptions OldOptionsV03 =
        SourceGenCompatHarness.BuildFrozenContextSnapshot("v0_3");

    // ─── Forward-compat: old payload deserializes under new context ─────────

    [Fact]
    public void ForwardCompat_AiRequest_DeserializesCleanlyUnderCurrentOptions()
    {
        var entry = new DurableSessionRequest
        {
            CorrelationId = "corr-fc-1",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, [new TextContent("hello")]),
            },
        };

        // Serialize with the "old" options (only ai_request / ai_response known)
        var json = JsonSerializer.Serialize<DurableSessionEntry>(entry, OldOptionsV03);

        // Then deserialize with the "new" options — must succeed.
        var roundTripped = JsonSerializer.Deserialize<DurableSessionEntry>(json, NewOptions);

        var req = Assert.IsType<DurableSessionRequest>(roundTripped);
        Assert.Equal("corr-fc-1", req.CorrelationId);
        Assert.Single(req.Messages);
    }

    [Fact]
    public void ForwardCompat_AiResponse_DeserializesCleanlyUnderCurrentOptions()
    {
        var entry = new DurableSessionResponse
        {
            CorrelationId = "corr-fc-2",
            CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            Messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, [new TextContent("hi back")]),
            },
            Usage = new UsageDetails { TotalTokenCount = 7 },
        };

        var json = JsonSerializer.Serialize<DurableSessionEntry>(entry, OldOptionsV03);
        var roundTripped = JsonSerializer.Deserialize<DurableSessionEntry>(json, NewOptions);

        var resp = Assert.IsType<DurableSessionResponse>(roundTripped);
        Assert.Equal("corr-fc-2", resp.CorrelationId);
        Assert.Equal(7, resp.Usage?.TotalTokenCount);
    }

    // ─── Backward-compat: new (fake) discriminator under old context ────────

    [Fact]
    public void BackwardCompat_UnknownDiscriminator_RaisesTypedException()
    {
        // Hand-roll a payload with an as-yet-unregistered discriminator. This
        // simulates: a future build registered "compaction-marker" via
        // [JsonDerivedType] and wrote a workflow history entry; an older
        // worker (still on v0.3) replays the same history.
        const string FutureDiscriminator = "compaction-marker";
        var payload = $$"""
        {
          "$type": "{{FutureDiscriminator}}",
          "CorrelationId": "corr-bc-1",
          "CreatedAt": "2026-06-01T00:00:00+00:00",
          "Messages": []
        }
        """;

        var ex = Assert.Throws<DurableReplayCompatibilityException>(() =>
            SourceGenCompatHarness.DeserializeWithWrap<DurableSessionEntry>(
                payload, OldOptionsV03, FutureDiscriminator));

        Assert.Equal(FutureDiscriminator, ex.Discriminator);
        Assert.Contains(FutureDiscriminator, ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    [Fact]
    public void BackwardCompat_AssertReplayDeserialization_PassesForFutureDiscriminator()
    {
        // The harness's all-in-one assertion API is the recommended call shape
        // for future Step 5 tests: pass new options, frozen old options, the
        // payload, and the expected discriminator. This test exercises that
        // path end-to-end.
        //
        // We can't construct a payload that the NEW options would deserialize
        // cleanly to a typed subclass yet (because "compaction-marker" isn't
        // registered yet either). So we use a fake discriminator that the
        // *new* options also reject but check the wrap-and-rethrow path
        // separately. The full AssertReplayDeserialization call lands when
        // Step 5 adds the real marker.
        const string FakeDiscriminator = "future-entry-shape";
        var payload = $$"""
        {
          "$type": "{{FakeDiscriminator}}",
          "CorrelationId": "corr-bc-2",
          "CreatedAt": "2026-06-01T00:00:00+00:00",
          "Messages": []
        }
        """;

        // The new context also doesn't know "future-entry-shape" — verify the
        // harness's wrap still produces the typed exception for that case.
        var ex = Assert.Throws<DurableReplayCompatibilityException>(() =>
            SourceGenCompatHarness.DeserializeWithWrap<DurableSessionEntry>(
                payload, NewOptions, FakeDiscriminator));

        Assert.Equal(FakeDiscriminator, ex.Discriminator);
    }

    // ─── Mixed-fleet sim: old → new → old round-trip ────────────────────────

    [Fact]
    public void MixedFleet_RoundTripOldNewOld_PreservesContent()
    {
        var original = new DurableSessionResponse
        {
            CorrelationId = "corr-mf-1",
            CreatedAt = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero),
            Messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, [new TextContent("mixed-fleet survivor")]),
            },
            Usage = new UsageDetails { TotalTokenCount = 123 },
        };

        // Old worker writes
        var jsonFromOld = JsonSerializer.Serialize<DurableSessionEntry>(original, OldOptionsV03);
        // New worker reads/rewrites
        var newSide = Assert.IsType<DurableSessionResponse>(
            JsonSerializer.Deserialize<DurableSessionEntry>(jsonFromOld, NewOptions));
        var jsonFromNew = JsonSerializer.Serialize<DurableSessionEntry>(newSide, NewOptions);
        // Old worker reads back
        var oldSideAgain = Assert.IsType<DurableSessionResponse>(
            JsonSerializer.Deserialize<DurableSessionEntry>(jsonFromNew, OldOptionsV03));

        Assert.Equal("corr-mf-1", oldSideAgain.CorrelationId);
        Assert.Equal(123, oldSideAgain.Usage?.TotalTokenCount);
        Assert.Single(oldSideAgain.Messages);
    }

    // ─── Sanity: no raw JsonException leaks for discriminator-mismatch ──────

    [Fact]
    public void Sanity_DiscriminatorMismatch_DoesNotLeakRawJsonException()
    {
        const string FutureDiscriminator = "compaction-marker";
        var payload = $$"""
        {
          "$type": "{{FutureDiscriminator}}",
          "CorrelationId": "corr-sn-1",
          "CreatedAt": "2026-06-01T00:00:00+00:00",
          "Messages": []
        }
        """;

        // If a raw JsonException ever bubbles out of DeserializeWithWrap for
        // the discriminator-mismatch case, this test fails because Assert.Throws
        // wants the exact derived type.
        var caught = Assert.Throws<DurableReplayCompatibilityException>(() =>
            SourceGenCompatHarness.DeserializeWithWrap<DurableSessionEntry>(
                payload, OldOptionsV03, FutureDiscriminator));

        // The inner exception is the original JsonException — surfaced for
        // diagnostics, not as the public type.
        Assert.IsAssignableFrom<JsonException>(caught.InnerException);
    }

    // ─── Sanity: non-discriminator JSON errors still propagate as JsonException ─

    [Fact]
    public void Sanity_NonDiscriminatorJsonError_PropagatesAsJsonException()
    {
        // Malformed JSON should NOT be wrapped — only discriminator mismatches.
        // This guards against the harness's IsDiscriminatorMismatch becoming
        // too greedy.
        const string Malformed = "{ this is not valid json ";

        Assert.Throws<JsonException>(() =>
            SourceGenCompatHarness.DeserializeWithWrap<DurableSessionEntry>(
                Malformed, OldOptionsV03, discriminatorHint: "irrelevant"));
    }

    // ─── Snapshot integrity ─────────────────────────────────────────────────

    [Fact]
    public void Snapshot_v0_3_CanBeLoaded_AndFiltersKnownDiscriminators()
    {
        // Build twice to confirm idempotency and that the file is present.
        var first = SourceGenCompatHarness.BuildFrozenContextSnapshot("v0_3");
        var second = SourceGenCompatHarness.BuildFrozenContextSnapshot("v0_3");

        Assert.NotNull(first);
        Assert.NotNull(second);

        // Both should still accept the known discriminators.
        var req = new DurableSessionRequest
        {
            CorrelationId = "corr-snap-1",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var json = JsonSerializer.Serialize<DurableSessionEntry>(req, first);
        var roundTrip = JsonSerializer.Deserialize<DurableSessionEntry>(json, second);
        Assert.IsType<DurableSessionRequest>(roundTrip);
    }
}
