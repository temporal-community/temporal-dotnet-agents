using System.IO.Compression;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class PayloadCompressionEvidenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GzipExperiment_IsLosslessAndRecordsFixtureRatio(bool incompressible)
    {
        var original = CreateFixture(100, 4096, incompressible);

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(
            compressed,
            CompressionLevel.Fastest,
            leaveOpen: true))
        {
            gzip.Write(original);
        }

        compressed.Position = 0;
        using var gzipInput = new GZipStream(compressed, CompressionMode.Decompress);
        using var restored = new MemoryStream();
        gzipInput.CopyTo(restored);

        Assert.Equal(original, restored.ToArray());
        Assert.True(compressed.Length > 0);
        output.WriteLine(
            $"incompressible={incompressible}; raw={original.Length}; gzip={compressed.Length}; " +
            $"ratio={(double)compressed.Length / original.Length:P2}");
    }

    private static byte[] CreateFixture(int toolCount, int schemaBytes, bool incompressible)
    {
        var builder = new StringBuilder(toolCount * schemaBytes);
        var random = new Random(1729);
        for (var tool = 0; tool < toolCount; tool++)
        {
            builder.Append("{\"name\":\"tool_").Append(tool).Append("\",\"schema\":\"");
            for (var index = 0; index < schemaBytes; index++)
            {
                builder.Append(incompressible ? (char)random.Next(33, 127) : 'x');
            }
            builder.Append("\"}");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
