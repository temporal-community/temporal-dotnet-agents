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
            ["AuthService.cs"] = """
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

            ["UserRepository.cs"] = """
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

            ["OrderController.cs"] = """
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
        [Description("The filename to read, e.g. AuthService.cs")] string filename)
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
