using TemporalCommunity.Extensions.AI.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class ReferenceComparerTests
{
    [Fact]
    public void DistinctValueEqualInstances_AreDistinctSetEntries()
    {
        var first = new ValueEqualObject();
        var second = new ValueEqualObject();
        var entries = new HashSet<ValueEqualObject>(ReferenceComparer<ValueEqualObject>.Instance)
        {
            first,
            second,
        };

        Assert.False(ReferenceEquals(first, second));
        Assert.Equal(2, entries.Count);
        Assert.Contains(first, entries);
        Assert.Contains(second, entries);
    }

    [Fact]
    public void SameInstance_IsEqual()
    {
        var value = new ValueEqualObject();

        Assert.True(ReferenceComparer<ValueEqualObject>.Instance.Equals(value, value));
    }

    private sealed class ValueEqualObject
    {
        public override bool Equals(object? obj) => obj is ValueEqualObject;

        public override int GetHashCode() => 0;
    }
}
