using System.Reflection;
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.Agents.Skills;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Internal;

/// <summary>
/// Base-contract guard suite for <c>TemporalCommunity.Extensions.Agents</c> (plan §2.2 / §2.4,
/// findings F-2a / F-2b / F-2c).
/// </summary>
/// <remarks>
/// <para>
/// These are fast, no-server CI canaries that turn a <i>silent</i> Microsoft.Agents.AI (MAF)
/// base-library bump into a <i>red</i> unit test. Each guard pins one upstream contract that our
/// production code reflects on or string-matches — contracts the compiler cannot protect because
/// the members are <c>protected</c>, <c>internal</c>, or <c>internal sealed</c>.
/// </para>
/// <para>
/// <b>Single-source rule.</b> The FICC guard asserts the <i>production constant</i>
/// (<see cref="AgentInternalConstants.FunctionInvocationDelegatingAgentFullName"/>) resolves to a
/// real type — it never re-declares the FQN.
/// </para>
/// </remarks>
public class BaseContractGuardTests
{
    // -----------------------------------------------------------------------------------------
    // S-F-2a (MAF) — DelegatingAIAgent.InnerAgent still resolves.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Pins the protected <c>DelegatingAIAgent.InnerAgent</c> property that
    /// <c>AgentChainWalker</c> reflects by string name (AgentChainWalker.cs:73-76) to walk the
    /// <see cref="AIAgent"/> decorator chain.
    /// </summary>
    /// <remarks>
    /// If MAF renames or removes this protected member, the chain-walk primary degrades silently
    /// to the <c>GetService&lt;T&gt;()</c> fallback — FICC-conflict detection (the per-tool
    /// durability guard) and OTel suppression detection quietly stop seeing inner agent links.
    /// We reflect with the SAME binding flags the production walker uses so this fails for exactly
    /// the reason the walker would break.
    /// </remarks>
    [Fact]
    public void Maf_DelegatingAIAgent_InnerAgent_StillResolves()
    {
        var prop = typeof(DelegatingAIAgent).GetProperty(
            "InnerAgent",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(prop);
        Assert.True(
            typeof(AIAgent).IsAssignableFrom(prop!.PropertyType),
            $"DelegatingAIAgent.InnerAgent resolved but its type '{prop.PropertyType}' is no " +
            "longer assignable to AIAgent — AgentChainWalker.WalkAIAgent would break.");
    }

    // -----------------------------------------------------------------------------------------
    // S-F-2a (behavioral note) — detection consumers check PRESENCE, not which instance.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Behavioral-note guard backing S-F-2a (plan §2.2): documents and pins that the chain-walk
    /// detection consumers care only about <i>presence</i> of a link type, never about <i>which</i>
    /// instance the walk returns — so the "chain-walk primary, <c>GetService&lt;T&gt;()</c> fallback"
    /// ordering in <c>AgentChainWalker.FindFirst&lt;T&gt;</c> is safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verified in production at:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>AgentActivities.cs:752</c> — FICC conflict throws when any chain
    ///   link's <c>Type.FullName</c> matches; the offending instance is never used.</description></item>
    ///   <item><description><c>AgentActivities.cs:772-774</c> — OTel suppression is a boolean OR of
    ///   <c>Contains&lt;OpenTelemetryAgent&gt;</c> / <c>Contains&lt;OpenTelemetryChatClient&gt;</c>;
    ///   only presence drives <c>suppressAgentTurnSpan</c>.</description></item>
    ///   <item><description><c>DurableChatActivities.cs:130,487</c> +
    ///   <c>DurableMixedPatternValidator.cs:122</c> — FICC (<c>FunctionInvokingChatClient</c>)
    ///   detection is <c>Contains&lt;T&gt;</c> (boolean).</description></item>
    /// </list>
    /// <para>
    /// This guard asserts the public <c>Contains&lt;T&gt;</c> contract is boolean — if a future
    /// change made detection instance-sensitive, the walk-vs-fallback order would matter and this
    /// note would need revisiting.
    /// </para>
    /// </remarks>
    [Fact]
    public void AgentChainWalker_Detection_IsPresenceBased_NotInstanceSensitive()
    {
        // The detection API the consumers use returns bool (presence), confirming consumers are
        // indifferent to which instance the dual-traversal returns.
        var containsAgent = typeof(AgentChainWalker)
            .GetMethod(
                nameof(AgentChainWalker.Contains),
                [typeof(AIAgent)]);

        Assert.NotNull(containsAgent);
        Assert.Equal(typeof(bool), containsAgent!.ReturnType);
    }

    // -----------------------------------------------------------------------------------------
    // S-F-2c — FICC FQN resolves to a real Type in the loaded MAF assembly.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts the production constant
    /// <see cref="AgentInternalConstants.FunctionInvocationDelegatingAgentFullName"/> resolves to a
    /// real <see cref="Type"/> in the same MAF assembly that defines the public
    /// <see cref="ChatClientAgent"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MAF's <c>FunctionInvocationDelegatingAgent</c> is <c>internal sealed</c>, so it cannot be
    /// referenced via <c>typeof()</c>. Our FICC-conflict detection
    /// (<c>DurableAgentPipelineValidator</c> + <c>AgentActivities</c>) matches it by
    /// <c>Type.FullName</c> using this single-sourced constant. If MAF renames the internal type,
    /// the FQN match silently stops firing and our per-tool-durability guard is disabled with no
    /// signal. This test resolves the FQN against the MAF assembly (located via the public
    /// <see cref="ChatClientAgent"/>, so we never hardcode an assembly name either) and fails if it
    /// no longer maps to a type.
    /// </para>
    /// </remarks>
    [Fact]
    public void Maf_FunctionInvocationDelegatingAgent_Fqn_ResolvesToRealType()
    {
        // Use the assembly that defines a known public MAF type — never a hardcoded assembly name.
        var mafAssembly = typeof(ChatClientAgent).Assembly;

        var ficcType = mafAssembly.GetType(
            AgentInternalConstants.FunctionInvocationDelegatingAgentFullName,
            throwOnError: false,
            ignoreCase: false);

        Assert.True(
            ficcType is not null,
            $"The production FICC constant '{AgentInternalConstants.FunctionInvocationDelegatingAgentFullName}' " +
            $"no longer resolves to a type in assembly '{mafAssembly.GetName().Name}'. MAF renamed or " +
            "removed the internal function-invocation decorator — our FICC-conflict detection (the " +
            "per-tool-durability guard) is now silently disabled. Update AgentInternalConstants.");

        // Sanity: the resolved type is a DelegatingAIAgent (it decorates an inner agent), matching
        // the chain-walk + FullName match our detection relies on.
        Assert.True(
            typeof(DelegatingAIAgent).IsAssignableFrom(ficcType!),
            $"FICC type '{ficcType}' resolved but is no longer a DelegatingAIAgent — the chain-walk " +
            "detection in AgentActivities would no longer encounter it as a chain link.");
    }

}
