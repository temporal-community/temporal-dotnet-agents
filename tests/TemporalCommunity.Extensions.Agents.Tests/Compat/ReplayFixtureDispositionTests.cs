using System.Text.Json;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Compat;

public class ReplayFixtureDispositionTests
{
    private static readonly string CompatDirectory =
        Path.Combine(AppContext.BaseDirectory, "Compat");

    [Fact]
    public void EveryCheckedInHistoryHasExplicitDisposition()
    {
        var fixtures = Directory
            .EnumerateFiles(Path.Combine(CompatDirectory, "Histories"), "*.json")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var path = Path.Combine(CompatDirectory, "replay-fixture-dispositions.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var dispositions = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.Ordinal);

        Assert.Equal(fixtures.Order(StringComparer.Ordinal), dispositions.Keys.Order(StringComparer.Ordinal));
        Assert.All(dispositions, entry => Assert.Equal("ReplaySuccess", entry.Value));
    }
}
