using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class ChatClientActivityTagsTests
{
    [Fact]
    public void Apply_SetsEveryConfiguredTagOnCurrentActivity()
    {
        using var source = new ActivitySource(Guid.NewGuid().ToString());
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var options = new ChatOptions()
            .WithChatClientTag("tenant", "acme")
            .WithChatClientTag("request_id", "req-1");
        using var activity = source.StartActivity("test");

        ChatClientActivityTags.Apply(options, NullLogger.Instance);

        Assert.NotNull(activity);
        Assert.Equal("acme", activity.GetTagItem("tenant"));
        Assert.Equal("req-1", activity.GetTagItem("request_id"));
    }

    [Fact]
    public void Apply_WithNoCurrentActivity_DoesNotThrow()
    {
        var options = new ChatOptions().WithChatClientTag("tenant", "acme");

        var exception = Record.Exception(
            () => ChatClientActivityTags.Apply(options, NullLogger.Instance));

        Assert.Null(exception);
    }

    [Fact]
    public void Apply_WithNoTags_DoesNotModifyCurrentActivity()
    {
        using var activity = new Activity("test").Start();

        ChatClientActivityTags.Apply(new ChatOptions(), NullLogger.Instance);

        Assert.Empty(activity.Tags);
    }
}
