using TemporalCommunity.Extensions.Agents.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Session;

/// <summary>
/// Option D Phase 1d — the run guard that makes session-owned state safe. The guard is
/// per-session on purpose: one agent instance must stay free to drive several distinct sessions
/// at once, while two runs over the <em>same</em> session are rejected.
/// </summary>
/// <remarks>
/// These cover the guard's own state machine. The end-to-end proof — that
/// <c>TemporalAIAgent.RunCoreAsync</c> enters before scheduling an activity and exits on every
/// completion path — lives in the integration suite, since the agent only runs inside a workflow.
/// </remarks>
public class TemporalAgentSessionRunGuardTests
{
    private static TemporalAgentSession NewSession(string key = "abc123") =>
        new(new TemporalAgentSessionId("Assistant", key));

    [Fact]
    public void EnterRun_SecondOverlappingEntry_Throws()
    {
        var session = NewSession();
        session.EnterRun();

        var ex = Assert.Throws<InvalidOperationException>(session.EnterRun);
        Assert.Contains("same session", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnterRun_AfterExit_IsAllowed()
    {
        // Sequential turns on one session are the normal case and must not be blocked.
        var session = NewSession();
        session.EnterRun();
        session.ExitRun();
        session.EnterRun();
        session.ExitRun();
    }

    [Fact]
    public void EnterRun_AfterAFailedRunExited_IsAllowed()
    {
        // ExitRun runs in a finally, so a turn that threw must leave the session reusable rather
        // than permanently wedged.
        var session = NewSession();
        try
        {
            session.EnterRun();
            throw new InvalidOperationException("simulated turn failure");
        }
        catch (InvalidOperationException)
        {
            session.ExitRun();
        }

        session.EnterRun();
    }

    [Fact]
    public void ExitRun_WithoutEnter_IsHarmless()
    {
        NewSession().ExitRun();
    }

    [Fact]
    public void DistinctSessions_RunIndependently()
    {
        // The whole point of putting the flag on the session: parallel conversations on one agent.
        var a = NewSession("aaa");
        var b = NewSession("bbb");

        a.EnterRun();
        b.EnterRun();

        a.ExitRun();
        b.ExitRun();
    }

    [Fact]
    public void RunGuard_IsNotCarriedThroughSerialization()
    {
        // A restored session is by definition not mid-run; carrying the flag across a
        // continue-as-new boundary would deadlock the next turn.
        var session = NewSession();
        session.EnterRun();

        var restored = TemporalAgentSession.Deserialize(session.Serialize());

        restored.EnterRun();
    }
}
