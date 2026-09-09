// FakeFileSystem — three mock C# source files with realistic-looking content.
// No real filesystem dependency — runs identically in CI and locally.

using System.ComponentModel;

namespace WorkingSet;

/// <summary>
/// In-memory file system with three mock C# source files.
/// Register as a singleton so the same instance is shared across the session.
/// </summary>
public sealed class FakeFileSystem
{
    private static readonly Dictionary<string, string> s_files =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Auth/AuthService.cs"] = """
                public class AuthService
                {
                    private readonly IUserRepository _users;
                    public AuthService(IUserRepository users) => _users = users;

                    public async Task<ClaimsPrincipal?> ValidateTokenAsync(string jwt)
                    {
                        var userId = ParseUserIdFromJwt(jwt);
                        var user = await _users.GetUserByIdAsync(userId);
                        return user is null ? null : BuildPrincipal(user);
                    }

                    private static string ParseUserIdFromJwt(string jwt) => /* decode */ jwt;
                    private static ClaimsPrincipal BuildPrincipal(User u) => new();
                }
                """,

            ["src/Data/UserRepository.cs"] = """
                public class UserRepository : IUserRepository
                {
                    private readonly AppDbContext _db;
                    public UserRepository(AppDbContext db) => _db = db;

                    public Task<User?> GetUserByIdAsync(string id) =>
                        _db.Users.FirstOrDefaultAsync(u => u.Id == id);

                    public Task<User?> FindByEmailAsync(string email) =>
                        _db.Users.FirstOrDefaultAsync(u => u.Email == email);

                    public Task AddAsync(User user)
                    {
                        _db.Users.Add(user);
                        return _db.SaveChangesAsync();
                    }
                }
                """,

            ["src/Api/OrderController.cs"] = """
                [ApiController]
                [Route("api/orders")]
                public class OrderController : ControllerBase
                {
                    private readonly IOrderService _orders;
                    private readonly AuthService _auth;

                    public OrderController(IOrderService orders, AuthService auth)
                    {
                        _orders = orders;
                        _auth = auth;
                    }

                    [HttpGet("{id}")]
                    public async Task<IActionResult> GetOrder(string id)
                    {
                        var principal = await _auth.ValidateTokenAsync(
                            Request.Headers.Authorization.ToString().Replace("Bearer ", ""));
                        if (principal is null) return Unauthorized();
                        var order = await _orders.GetByIdAsync(id);
                        return order is null ? NotFound() : Ok(order);
                    }
                }
                """,
        };

    /// <summary>
    /// Returns the content of the requested file, or a "file not found" message.
    /// </summary>
    [Description("Read the contents of a source file by its filename.")]
    public string ReadFile(
        [Description("The repository-relative path to read, e.g. src/Auth/AuthService.cs")] string filename)
    {
        return s_files.TryGetValue(filename, out var content)
            ? content
            : $"File not found: {filename}";
    }

    /// <summary>
    /// Returns the list of all files available in this fake repository.
    /// </summary>
    [Description("List all files available in this repository.")]
    public string[] ListFiles() =>
        [.. s_files.Keys];
}

/// <summary>
/// Sample-only observer: prints the working set that <c>WorkingSetContextProvider</c> stored in
/// the session StateBag, so this sample proves its own behaviour without depending on how the
/// model chooses to phrase an answer.
/// </summary>
/// <remarks>
/// This is also the documented pattern for reading the working set from downstream code — the
/// StateBag key mirrors the current working set for exactly this purpose. A real consumer would
/// act on the value rather than write to stdout.
/// </remarks>
public sealed class WorkingSetEchoProvider : Microsoft.Agents.AI.AIContextProvider
{
    protected override ValueTask<Microsoft.Agents.AI.AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Session is TemporalCommunity.Extensions.Agents.Session.TemporalAgentSession session
            && session.StateBag.TryGetValue(
                TemporalCommunity.Extensions.Agents.WorkingSetContextProvider.StateBagKey,
                out string? csv,
                System.Text.Json.JsonSerializerOptions.Default)
            && !string.IsNullOrEmpty(csv))
        {
            Console.WriteLine($"[WorkingSet] {csv}");
        }

        return new ValueTask<Microsoft.Agents.AI.AIContext>(new Microsoft.Agents.AI.AIContext());
    }
}
