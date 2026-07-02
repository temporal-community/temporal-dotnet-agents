// DateTimeContextProvider.cs — a simple stateless AIContextProvider that injects the
// current UTC date/time as an instruction before each LLM call.
//
// This provider does NOT own any tools. In the WeatherAgent registration it is paired
// with an explicit DurableToolRegistrationSpec (Approach B) to demonstrate that a
// provider-plus-tool registration does not require IDurableToolSource.

using Microsoft.Agents.AI;

namespace DurableContextProvider;

/// <summary>
/// Injects the current UTC date/time as an additional instruction before each LLM call.
/// Stateless — no StateBag writes needed because the timestamp is re-derived each step.
/// </summary>
public sealed class DateTimeContextProvider : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var utcNow = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(new AIContext
        {
            Instructions = $"Current UTC date and time: {utcNow:yyyy-MM-dd HH:mm} UTC.",
        });
    }
}
