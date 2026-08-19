using Temporalio.Api.WorkflowService.V1;
using Temporalio.Testing;

namespace TemporalCommunity.Extensions.Tests.Shared;

/// <summary>Creates embedded Temporal environments at the repository's minimum service baseline.</summary>
public static class TemporalServiceTestEnvironment
{
    /// <summary>The minimum Temporal Service version supported by this repository.</summary>
    public static readonly Version MinimumTemporalServiceVersion = new(1, 31, 0);

    /// <summary>Temporal CLI release whose embedded server is 1.31.2.</summary>
    public const string TemporalCliDownloadVersion = "v1.8.0";

    /// <summary>Environment variable containing a pre-verified Temporal CLI executable.</summary>
    public const string TemporalTestServerPathEnvironmentVariable = "TEMPORAL_TEST_SERVER_PATH";

    /// <summary>Starts and verifies an embedded Temporal Service 1.31.x environment.</summary>
    public static async Task<WorkflowEnvironment> StartLocalAsync(params string[] extraArgs)
    {
        var existingPath = Environment.GetEnvironmentVariable(
            TemporalTestServerPathEnvironmentVariable);
        var devServerOptions = new DevServerOptions
        {
            ExtraArgs = extraArgs,
        };
        if (string.IsNullOrWhiteSpace(existingPath))
        {
            devServerOptions.DownloadVersion = TemporalCliDownloadVersion;
        }
        else
        {
            devServerOptions.ExistingPath = existingPath;
        }

        var environment = await WorkflowEnvironment.StartLocalAsync(new()
        {
            DownloadDirectory = GetDownloadDirectory(),
            DevServerOptions = devServerOptions,
        }).ConfigureAwait(false);

        try
        {
            var response = await environment.Client.WorkflowService.GetSystemInfoAsync(
                new GetSystemInfoRequest()).ConfigureAwait(false);
            var detected = ParseAndValidateServerVersion(response.ServerVersion);
            Console.WriteLine(
                "Temporal test service version: {0} (minimum {1})",
                detected,
                MinimumTemporalServiceVersion);
            return environment;
        }
        catch
        {
            await environment.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts the SDK's time-skipping test server for tests defined only by workflow timers.
    /// Transport, worker-restart, and activity-retry tests should continue to use
    /// <see cref="StartLocalAsync(string[])"/>.
    /// </summary>
    public static Task<WorkflowEnvironment> StartTimeSkippingAsync() =>
        WorkflowEnvironment.StartTimeSkippingAsync(new()
        {
            DownloadDirectory = GetDownloadDirectory(),
        });

    private static string GetDownloadDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "temporal-agents-test-servers");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Parses and validates a Temporal Service version returned by GetSystemInfo.</summary>
    public static Version ParseAndValidateServerVersion(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            throw UnsupportedVersion(serverVersion, "the service returned an empty version");
        }

        var numericPrefix = new string(serverVersion
            .TakeWhile(character => char.IsDigit(character) || character == '.')
            .ToArray())
            .TrimEnd('.');
        if (!Version.TryParse(numericPrefix, out var parsed)
            || parsed.Major < 0
            || parsed.Minor < 0)
        {
            throw UnsupportedVersion(serverVersion, "the version format was not recognized");
        }

        if (parsed < MinimumTemporalServiceVersion)
        {
            throw UnsupportedVersion(serverVersion, "the service is below the supported minimum");
        }

        return parsed;
    }

    private static InvalidOperationException UnsupportedVersion(string? detected, string reason) =>
        new(
            $"Temporal Service {MinimumTemporalServiceVersion} or newer is required; " +
            $"detected '{detected ?? "(missing)"}' ({reason}).");
}
