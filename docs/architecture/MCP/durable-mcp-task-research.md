# Durable MCP Task consumption research

Status: **Defer**. This ADR records executable, test-only research against MCP C# SDK 2.2. It does
not introduce a production dependency, package, public API, workflow/activity name, Update, Query,
or durable wire contract.

## Question

Should the managed durable AI loops directly consume long-running remote MCP Tasks by durably
coordinating Task start, polling, cancellation, and result retrieval?

Ordinary `McpClientTool` remains supported as an `AIFunction`. It invokes ordinary `tools/call`
inside one Temporal tool activity. That path does not opt into the MCP Tasks extension and is not
the subject of this ADR.

## Verified SDK boundary

`CallToolAsTaskAsync` exposes a created task ID; subsequent `GetTaskAsync` and `CancelTaskAsync`
operate on that ID. The stock `WithTasks` server persists Task records through `IMcpTaskStore`, but
executes the tool body in process-local work. A durable store therefore does not transfer execution
ownership to Temporal or recover an interrupted tool body. The stable upstream execution-owner
seam remains unresolved in `modelcontextprotocol/csharp-sdk#1820`.

Task creation has no standardized idempotency key. A lost start response is consequently ambiguous:
automatic activity retry can create a second remote Task. The prototypes set the start activity to
one attempt and surface ambiguity; this is at-least-once behavior, not exactly once.

`InputRequired` can represent elicitation or model sampling. It is intentionally rejected in this
phase because it requires a separate authorization, cost, payload, timeout, and Continue-as-New
contract. It is not equivalent to binary approval.

## Executable prototypes

Both prototypes use a real MCP 2.2 client/server over in-process pipes and Temporal Service 1.31.2.
They compile only into `TemporalCommunity.Extensions.AI.IntegrationTests`.

### A: in-parent coordinator

The workflow schedules a one-attempt start activity, records the returned Task ID, waits with
Temporal timers, and schedules bounded poll activities. Cancellation is a separate activity and
polling continues until a terminal state is observed.

Verified:

- a poll-activity retry retains the original Task ID and does not restart the Task;
- completion and cancellation reach explicit terminal results;
- the MCP server observes cooperative cancellation;
- no activity remains open during the polling interval;
- the completed workflow history replays; and
- ordinary `McpClientTool.InvokeAsync` fails before execution when the server requires Tasks,
  proving ordinary tool registration does not silently enable this lifecycle.

### B: detached child executor

The parent starts an executor child with `ParentClosePolicy.Abandon`, immediately continues as new,
and waits in its new run. The child owns start/timer/poll state and signals the stable parent workflow
ID after reaching a terminal result.

Verified:

- parent Continue-as-New does not cancel the detached executor;
- the completion signal reaches the new parent run;
- the remote Task is created once;
- first parent run, second parent run, and child histories replay independently; and
- the child enforces a finite poll cap and rejects `InputRequired` as unsupported.

## Comparison

| Concern | A: parent coordinator | B: detached child |
|---|---|---|
| Task ID durability | Parent history | Child history |
| Poll-history isolation | No | Yes |
| Parent Continue-as-New | Must carry every Task field and avoid active Update handlers | Parent carries only executor identity/result wait |
| Completion delivery | Direct return/state | Signal to stable parent identity |
| Cancellation | One workflow state machine | Cross-workflow cancellation and terminal signal races |
| Operational visibility | Fewer workflow executions | Independent executor workflow is easier to inspect but increases workflow count |
| Ambiguous start | Unresolved | Unresolved |
| MCP session/addressability after reconnect | Required | Required |
| `InputRequired` | Unsupported | Unsupported |
| Public contract burden | High | Higher: child identity, parent signaling, close policy, and recovery |

Prototype B provides materially better history isolation, but it does not remove the ambiguous-start,
remote addressability, authorization, or server execution-ownership problems. A child workflow is
therefore an implementation candidate, not a correctness solution by itself.

## Decision

**Defer** a production integration.

The prototypes prove that Temporal can durably coordinate a known MCP Task after its ID is recorded.
They do not prove safe Task creation, production multi-node addressability, `InputRequired`, stable
execution ownership on the MCP server, or two compatible non-approval use cases. Application-owned
workflows may use the demonstrated pattern when they can enforce server-side idempotency and Task
addressability. The stock managed AI loops will not absorb this state machine yet.

No research type may enter production projects, `PublicAPI.*.txt`, NuGet assets, or registered
package-owned workflow/activity names while this decision remains Defer. The core packages retain no
`ModelContextProtocol.Extensions.Tasks` dependency.

## Adoption exit criteria

An Adopt ADR requires all of the following:

1. A stable MCP server execution-owner seam that preserves normal authorization, filters, DI scope,
   schema checks, and error mapping—or an explicitly experimental package boundary.
2. A server-enforced, model-invisible idempotency contract for Task creation and a defined ambiguous
   start result.
3. Stateless/multi-node Task addressability across MCP client and server replacement.
4. Exact public asynchronous client/result types, wire versions, lifecycle transitions, bounds,
   cancellation races, authorization, and operational recovery.
5. Two concrete non-approval use cases with compatible requirements.
6. Bounded-history/Continue-as-New, restart, expiration, unknown Task, poll-error, and terminal-race
   coverage.
7. A separately approved `InputRequired` design, if supported at all.
8. Evidence that the lifecycle integrates once for MEAI and MAF rather than producing two divergent
   state machines.

Until those gates pass, generalized deferred tools and MCP Task consumption both remain Defer.
