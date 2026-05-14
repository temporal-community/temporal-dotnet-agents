using System.Diagnostics.CodeAnalysis;

namespace Temporalio.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when a <see cref="ChatOptions"/>-supplied
/// <see cref="TemporalChatOptionsExtensions.WithChatClientFactoryKey(Microsoft.Extensions.AI.ChatOptions, string)"/>
/// names a decorator key that is not registered in DI, or when
/// <see cref="DurableExecutionOptions.DefaultChatClientFactoryKey"/> points at a missing
/// registration.
/// </summary>
/// <remarks>
/// <para>
/// Per the Step 4 design (artifacts/maf-feature-gap-analysis.md → Per-Call
/// <c>ChatClientFactory</c>), registered <c>IChatClientDecorator</c> instances live under
/// keyed DI singletons. A per-call factory key with no matching DI registration is
/// a misconfiguration — surfacing it as a typed exception (rather than a vague
/// <c>KeyNotFoundException</c> from the DI container) gives operators actionable diagnostics.
/// </para>
/// <para>
/// Stable subtype (no <c>[Experimental]</c> attribute) — once an SDK release exposes
/// <c>WithChatClientFactoryKey</c>, this exception type is part of the public catch surface.
/// </para>
/// </remarks>
public sealed class DurableChatClientFactoryNotFoundException : DurableConfigurationException
{
    /// <summary>
    /// Gets the factory key that was requested but not found in DI. Wire-format-stable —
    /// these are <see cref="string"/> constants the user picked at registration time, not
    /// user data.
    /// </summary>
    public required string FactoryKey { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableChatClientFactoryNotFoundException"/>
    /// class with a default message derived from <paramref name="factoryKey"/>.
    /// </summary>
    [SetsRequiredMembers]
    public DurableChatClientFactoryNotFoundException(string factoryKey)
        : base(BuildMessage(factoryKey))
    {
        FactoryKey = factoryKey;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableChatClientFactoryNotFoundException"/>
    /// class with a specified error message.
    /// </summary>
    [SetsRequiredMembers]
    public DurableChatClientFactoryNotFoundException(string factoryKey, string message)
        : base(message)
    {
        FactoryKey = factoryKey;
    }

    private static string BuildMessage(string factoryKey) =>
        $"No IChatClientDecorator is registered under the key '{factoryKey}'. " +
        "Ensure the decorator is registered with " +
        "services.AddKeyedSingleton<IChatClientDecorator, YourDecorator>(\"" + factoryKey + "\") " +
        "before the worker host starts. Built-in keys (e.g. \"tags\") are pre-registered by " +
        "AddDurableAI / AddTemporalAgents.";
}
