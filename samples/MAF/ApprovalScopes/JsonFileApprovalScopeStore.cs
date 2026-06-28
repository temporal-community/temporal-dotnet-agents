using System.Text.Json;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.AI;

namespace ApprovalScopes;

/// <summary>
/// A file-backed <see cref="IApprovalScopeStore"/> that persists always-scope records as a JSON
/// array under <c>~/.temporalagents/approval-scopes/{safeAgentName}/{safeStoreKey}.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Suitable for local development and samples. For production use, implement
/// <see cref="IApprovalScopeStore"/> against a database or distributed cache.
/// </para>
/// <para>
/// Thread safety: a single <see cref="SemaphoreSlim"/> serializes all reads and writes per
/// store instance. Since this store is registered as a singleton, all sessions on the same
/// worker share the same lock.
/// </para>
/// <para>
/// This sample store is process-local. It sanitizes path components and uses atomic file
/// replacement, but it is not a distributed lock and should not be shared by multiple workers.
/// </para>
/// </remarks>
public sealed class JsonFileApprovalScopeStore : IApprovalScopeStore
{
    private readonly string _baseDir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileApprovalScopeStore()
    {
        _baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".temporalagents", "approval-scopes");
        Directory.CreateDirectory(_baseDir);
    }

    private string FilePath(string agentName, string storeKey)
    {
        var dir = Path.Combine(_baseDir, SafePathComponent(agentName));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{SafePathComponent(storeKey.Replace('.', '_'))}.json");
    }

    private static string SafePathComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch)
            .ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "_" : safe;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ApprovalScopeRecord>> LoadAsync(
        string agentName,
        string storeKey,
        CancellationToken cancellationToken = default)
    {
        var path = FilePath(agentName, storeKey);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path)) return [];
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<List<ApprovalScopeRecord>>(
                       json, DurableAIJsonUtilities.DefaultOptions) ?? [];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task AppendAsync(
        string agentName,
        string storeKey,
        ApprovalScopeRecord record,
        CancellationToken cancellationToken = default)
    {
        var path = FilePath(agentName, storeKey);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            List<ApprovalScopeRecord> records = [];
            if (File.Exists(path))
            {
                var existing = await File.ReadAllTextAsync(path, cancellationToken);
                records = JsonSerializer.Deserialize<List<ApprovalScopeRecord>>(
                              existing, DurableAIJsonUtilities.DefaultOptions) ?? [];
            }

            // Idempotency guard: skip if a record with the same originating request ID exists.
            if (records.Any(r => r.OriginatingRequestId == record.OriginatingRequestId))
                return;

            records.Add(record);

            var json = JsonSerializer.Serialize(records, DurableAIJsonUtilities.DefaultOptions);
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
