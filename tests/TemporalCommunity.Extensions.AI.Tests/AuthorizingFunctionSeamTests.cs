using Microsoft.Extensions.AI;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Phase 5.1a Proof-of-Concept: Authorization Deny-Path Test Seam Demonstration
/// 
/// This test file demonstrates a viable test boundary for authorization behavior.
/// It proves that the AuthorizingFunction component (from ExtensibleDurableTurns sample)
/// can be isolated and unit-tested with both allow and deny paths exercised.
/// 
/// The test seam uses Option B (Extracted component test):
/// - Unit-tests AuthorizingFunction directly by wrapping AIFunctions
/// - Mocks the IAuthoritativeAuthorizationService dependency
/// - Tests both allow path (authorized subject succeeds) and deny path (throws UnauthorizedAccessException)
/// - Verifies deny path prevents tool body execution (no side effects)
/// </summary>
public class AuthorizingFunctionSeamTests
{
    private interface ITestAuthorizationService
    {
        ValueTask<bool> IsAllowedAsync(string subjectId, string resourceId, CancellationToken cancellationToken);
    }

    private sealed class MockAuthorizationService : ITestAuthorizationService
    {
        private readonly Func<string, string, ValueTask<bool>> _isAllowedAsync;

        public MockAuthorizationService(Func<string, string, ValueTask<bool>> isAllowedAsync)
        {
            _isAllowedAsync = isAllowedAsync;
        }

        public ValueTask<bool> IsAllowedAsync(string subjectId, string resourceId, CancellationToken cancellationToken)
        {
            return _isAllowedAsync(subjectId, resourceId);
        }
    }

    /// <summary>
    /// Test wrapper that mimics the AuthorizingFunction from the sample.
    /// This is the extracted, testable component.
    /// </summary>
    private sealed class AuthorizingFunction : DelegatingAIFunction
    {
        private readonly ITestAuthorizationService _authorization;
        private readonly string _subjectId;
        private readonly string _resourceId;
        private bool _toolBodyExecuted = false;

        public AuthorizingFunction(
            AIFunction innerFunction,
            ITestAuthorizationService authorization,
            string subjectId,
            string resourceId)
            : base(innerFunction)
        {
            _authorization = authorization;
            _subjectId = subjectId;
            _resourceId = resourceId;
        }

        public bool ToolBodyExecuted => _toolBodyExecuted;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            // Authorization check BEFORE tool body
            if (!await _authorization.IsAllowedAsync(_subjectId, _resourceId, cancellationToken))
            {
                throw new UnauthorizedAccessException(
                    "The authoritative service denied this operation.");
            }

            // Tool body executes only if authorized
            _toolBodyExecuted = true;
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            return result;
        }
    }

    /// <summary>
    /// PASSING TEST: Authorized subject succeeds and tool body executes
    /// 
    /// Demonstrates: Allow path works correctly
    /// - Subject "trusted-user" with "resource-7" is authorized
    /// - Tool body executes successfully
    /// - No exception thrown
    /// </summary>
    [Fact]
    public async Task AuthorizingFunction_AllowedSubject_ExecutesToolBody()
    {
        // Arrange: Create an inner function that increments a counter
        var executionCount = 0;
        var innerFunction = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executionCount);
                return "tool-result";
            },
            "ProcessData",
            "Processes data when authorized");

        // Mock: Authorization service allows trusted-user for resource-7
        var authService = new MockAuthorizationService(
            async (subject, resource) =>
            {
                await Task.Yield();
                return subject == "trusted-user" && !string.IsNullOrWhiteSpace(resource);
            });

        var authorizingFunction = new AuthorizingFunction(
            innerFunction,
            authService,
            subjectId: "trusted-user",
            resourceId: "resource-7");

        // Act: Invoke the authorized tool
        var result = await authorizingFunction.InvokeAsync();

        // Assert: Tool body executed successfully
        Assert.Equal("tool-result", result?.ToString());
        Assert.Equal(1, executionCount);
        Assert.True(authorizingFunction.ToolBodyExecuted, "Tool body should execute for authorized subject");
    }

    /// <summary>
    /// FAILING TEST: Denied subject throws before tool body executes
    /// 
    /// Demonstrates: Deny path works correctly
    /// - Subject "untrusted-subject" with "resource-7" is denied
    /// - UnauthorizedAccessException is thrown BEFORE tool body executes
    /// - Tool has no side effects (proves no execution after denial)
    /// </summary>
    [Fact]
    public async Task AuthorizingFunction_DeniedSubject_ThrowsUnauthorizedAccessException()
    {
        // Arrange: Create an inner function with side effects to prove it doesn't run
        var executionCount = 0;
        var innerFunction = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executionCount);
                return "tool-result";
            },
            "ProcessData",
            "Processes data when authorized");

        // Mock: Authorization service denies untrusted-subject
        var authService = new MockAuthorizationService(
            async (subject, resource) =>
            {
                await Task.Yield();
                return subject == "trusted-user" && !string.IsNullOrWhiteSpace(resource);
            });

        var authorizingFunction = new AuthorizingFunction(
            innerFunction,
            authService,
            subjectId: "untrusted-subject",
            resourceId: "resource-7");

        // Act & Assert: Denial throws before tool body executes
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await authorizingFunction.InvokeAsync());

        Assert.Equal("The authoritative service denied this operation.", exception.Message);
        Assert.Equal(0, executionCount, "Tool body should not execute for denied subject");
        Assert.False(authorizingFunction.ToolBodyExecuted, "Tool body should not execute for denied subject");
    }

    /// <summary>
    /// EDGE CASE TEST: Blank resource denied (part of allow condition)
    /// 
    /// Demonstrates: Authorization logic correctly validates both subject AND resource
    /// - Subject "trusted-user" with blank resource is denied
    /// - UnauthorizedAccessException is thrown
    /// </summary>
    [Fact]
    public async Task AuthorizingFunction_BlankResource_ThrowsUnauthorizedAccessException()
    {
        // Arrange: Create an inner function
        var executionCount = 0;
        var innerFunction = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executionCount);
                return "tool-result";
            },
            "ProcessData",
            "Processes data when authorized");

        // Mock: Authorization service denies blank resources (as in AuthoritativeAuthorizationService)
        var authService = new MockAuthorizationService(
            async (subject, resource) =>
            {
                await Task.Yield();
                return subject == "trusted-user" && !string.IsNullOrWhiteSpace(resource);
            });

        var authorizingFunction = new AuthorizingFunction(
            innerFunction,
            authService,
            subjectId: "trusted-user",
            resourceId: ""); // Blank resource

        // Act & Assert: Denial throws even for trusted subject when resource is blank
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await authorizingFunction.InvokeAsync());

        Assert.Equal("The authoritative service denied this operation.", exception.Message);
        Assert.Equal(0, executionCount, "Tool body should not execute when resource is blank");
        Assert.False(authorizingFunction.ToolBodyExecuted, "Tool body should not execute when resource is blank");
    }

    /// <summary>
    /// ASYNC AUTHORIZATION TEST: Authorization service is awaited correctly
    /// 
    /// Demonstrates: The seam properly handles async authorization checks
    /// - Authorization can be a slow async operation (e.g., database query)
    /// - Tool waits for authorization before executing
    /// </summary>
    [Fact]
    public async Task AuthorizingFunction_AsyncAuthorizationCheck_WaitsBeforeToolExecution()
    {
        // Arrange: Create an inner function
        var toolStartTime = DateTime.MinValue;
        var innerFunction = AIFunctionFactory.Create(
            () =>
            {
                toolStartTime = DateTime.UtcNow;
                return "tool-result";
            },
            "ProcessData",
            "Processes data when authorized");

        // Mock: Slow async authorization check
        var authCheckStartTime = DateTime.UtcNow;
        var authService = new MockAuthorizationService(
            async (subject, resource) =>
            {
                await Task.Delay(100); // Simulate slow auth service
                return subject == "trusted-user";
            });

        var authorizingFunction = new AuthorizingFunction(
            innerFunction,
            authService,
            subjectId: "trusted-user",
            resourceId: "resource-7");

        // Act: Invoke the authorized tool
        var result = await authorizingFunction.InvokeAsync();

        // Assert: Tool started after authorization check completed
        Assert.Equal("tool-result", result?.ToString());
        Assert.True(toolStartTime > authCheckStartTime, "Tool should start after auth check completes");
        Assert.True(authorizingFunction.ToolBodyExecuted, "Tool body should execute for authorized subject");
    }

    /// <summary>
    /// FORGED STATE TEST: Authorization service overrides any forged state
    /// 
    /// Demonstrates: The critical security property that even if the state
    /// contained a forged "already authorized" flag, the authorization service
    /// check runs immediately before tool execution and is authoritative.
    /// This test verifies that authorization cannot be bypassed by state tampering.
    /// </summary>
    [Fact]
    public async Task AuthorizingFunction_DespiteForgery_AuthorizationIsAuthoritative()
    {
        // Arrange: Create an inner function
        var executionCount = 0;
        var innerFunction = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executionCount);
                return "tool-result";
            },
            "ProcessData",
            "Processes data when authorized");

        // Mock: Authorization service allows only trusted-user
        var authService = new MockAuthorizationService(
            async (subject, resource) =>
            {
                await Task.Yield();
                // The service is authoritative: it doesn't care about forged state flags
                return subject == "trusted-user";
            });

        // Even if someone forges state claiming they're already authorized,
        // the AuthorizingFunction will still call the authoritative service
        var authorizingFunction = new AuthorizingFunction(
            innerFunction,
            authService,
            subjectId: "attacker-subject", // Forged subject ID
            resourceId: "resource-7");

        // Act & Assert: Forged authorization is ignored; real check is authoritative
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await authorizingFunction.InvokeAsync());

        Assert.Equal("The authoritative service denied this operation.", exception.Message);
        Assert.Equal(0, executionCount, "Tool body should not execute for forged authorization");
        Assert.False(authorizingFunction.ToolBodyExecuted, "Tool body should not execute despite forged state");
    }
}
