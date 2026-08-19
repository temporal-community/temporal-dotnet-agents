using System.Text.Json;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Compat;

public class ReplayFixtureDispositionTests
{
    private static readonly string CompatDirectory =
        Path.Combine(AppContext.BaseDirectory, "Compat");

    [Fact]
    public void EveryCheckedInHistoryHasExplicitDisposition()
    {
        var fixtureNames = Directory
            .EnumerateFiles(Path.Combine(CompatDirectory, "Histories"), "*.json")
            .Select(Path.GetFileName)
            .OfType<string>();
        var dispositions = LoadDispositions();

        var errors = ValidateFixtureCatalog(fixtureNames, dispositions);

        Assert.Empty(errors);
    }

    [Fact]
    public void UnclassifiedFixtureFailsCatalogValidation()
    {
        var dispositions = new Dictionary<string, ReplayFixtureDisposition>
        {
            ["known.json"] = ReplayFixtureDisposition.ReplaySuccess,
        };

        var errors = ValidateFixtureCatalog(["known.json", "orphan.json"], dispositions);

        Assert.Equal(["History fixture 'orphan.json' has no disposition."], errors);
    }

    internal static IReadOnlyList<string> ValidateFixtureCatalog(
        IEnumerable<string> fixtureNames,
        IReadOnlyDictionary<string, ReplayFixtureDisposition> dispositions)
    {
        var fixtureSet = fixtureNames.ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var fixture in fixtureSet.Order(StringComparer.Ordinal))
        {
            if (!dispositions.ContainsKey(fixture))
            {
                errors.Add($"History fixture '{fixture}' has no disposition.");
            }
        }

        foreach (var entry in dispositions.Keys.Order(StringComparer.Ordinal))
        {
            if (!fixtureSet.Contains(entry))
            {
                errors.Add($"Disposition '{entry}' has no checked-in history fixture.");
            }
        }

        return errors;
    }

    private static IReadOnlyDictionary<string, ReplayFixtureDisposition> LoadDispositions()
    {
        var path = Path.Combine(CompatDirectory, "replay-fixture-dispositions.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => Enum.Parse<ReplayFixtureDisposition>(property.Value.GetString()!, ignoreCase: false),
            StringComparer.Ordinal);
    }

    internal enum ReplayFixtureDisposition
    {
        ReplaySuccess,
        ExpectedNondeterminism,
        ContractOnly,
    }
}
