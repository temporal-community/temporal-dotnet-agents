// FakeFileSystem — an in-memory file store used by the ToolInterceptor sample.
// ReadFile and DeleteFile are the tool implementations registered via AIFunctionFactory.
// These methods run inside Temporal activities (not workflow code), so standard I/O is fine.

using System.ComponentModel;

/// <summary>
/// In-memory file store for the ToolInterceptor sample.
/// Holds three files: one readable config, one notes file, and one protected lock file.
/// </summary>
internal sealed class FakeFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["config.json"]  = """{ "version": "1.4.2", "debug": false, "maxRetries": 3 }""",
        ["notes.txt"]    = "Meeting notes: review Q2 roadmap, assign action items, schedule follow-up.",
        ["system.lock"]  = "LOCKED by kernel supervisor — do not delete.",
    };

    /// <summary>
    /// Returns the contents of the named file, or an error message if the file does not exist.
    /// Registered as the <c>read_file</c> tool — safe read-only operation, no gate needed.
    /// </summary>
    public string ReadFile(
        [Description("The name of the file to read (e.g. config.json)")]
        string name)
    {
        return _files.TryGetValue(name, out var content)
            ? content
            : $"Error: file '{name}' not found.";
    }

    /// <summary>
    /// Deletes the named file and returns a confirmation message.
    /// Registered as the <c>delete_file</c> tool — write operation; uses <c>NoRetry()</c>
    /// and <c>RequireApproval()</c> in the worker configuration.
    /// </summary>
    public string DeleteFile(
        [Description("The name of the file to delete (e.g. config.json)")]
        string name)
    {
        if (!_files.Remove(name))
            return $"Error: file '{name}' not found or already deleted.";

        return $"File '{name}' has been permanently deleted.";
    }
}
