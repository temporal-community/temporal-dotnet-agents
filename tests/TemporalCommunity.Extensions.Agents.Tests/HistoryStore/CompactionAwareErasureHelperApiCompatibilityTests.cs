#pragma warning disable TA002 // Compatibility test consumes the experimental erasure helper.

using TemporalCommunity.Extensions.Agents.HistoryStore;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.HistoryStore;

public class CompactionAwareErasureHelperApiCompatibilityTests
{
    [Fact]
    public void EraseSessionDataAsync_RetainsSetAndEnumerableOverloadsOnNet10()
    {
        var parameterTypes = typeof(CompactionAwareErasureHelper)
            .GetMethods()
            .Where(method => method.Name == nameof(CompactionAwareErasureHelper.EraseSessionDataAsync))
            .Select(method => method.GetParameters()[2].ParameterType)
            .ToList();

        Assert.Contains(typeof(IReadOnlySet<string>), parameterTypes);
        Assert.Contains(typeof(IEnumerable<string>), parameterTypes);
    }
}
