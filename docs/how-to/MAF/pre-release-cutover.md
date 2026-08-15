# Pre-release worker cutover

Use this runbook for a pre-release upgrade that changes `AgentWorkflow`, `AgentJobWorkflow`, or
application workflow behavior around `TemporalAIAgent`. The current correction intentionally does
not preserve the defective tool-selection behavior with workflow version markers. Do not run old
and corrected workers on the same affected task queue, and do not let corrected workers replay
pre-change histories.

Tool selection controls what the model is offered and what package workflows dispatch. It is
exposure control, not authorization. Every effectful tool must still authorize against current,
authoritative application data immediately before the effect.

## Compatibility matrix

| Worker/history combination | Supported during this cutover |
|---|---:|
| Old worker with pre-change running history | Only while draining before deployment |
| Corrected worker with a newly started corrected-version workflow | Yes |
| Corrected worker replaying pre-change history | No |
| Old worker replaying corrected-version history | No |
| Old and corrected workers polling the same affected queue | No |

The repository replay gates cover corrected-version histories containing blocked tool calls. They
do not promise cross-version replay for this pre-release behavior change.

## Before the maintenance window

1. Inventory every task queue that hosts package-owned `AgentWorkflow` or `AgentJobWorkflow` types.
2. Inventory every application-owned workflow type that calls `TemporalAIAgent`. The library cannot
   discover those containing workflow types generically.
3. Inventory Temporal Schedules and services that start delayed or fire-and-forget agent jobs.
4. Record the currently deployed application/package version so rollback can restore the complete
   old client/worker set, not only a worker binary.
5. Confirm operators can stop producers, pause Schedules, call graceful session shutdown, and
   terminate remaining executions.

All commands below use the installed Temporal CLI syntax. Add the environment's `--address`,
`--namespace`, authentication, and TLS flags.

## Cutover procedure

1. Stop external callers from creating sessions or submitting Updates.
2. Pause every Temporal Schedule that can start an affected workflow:

   ```bash
   temporal schedule toggle \
     --schedule-id "SCHEDULE_ID" \
     --pause \
     --reason "Pre-release agent worker cutover"
   ```

3. Stop services that create delayed or fire-and-forget agent starts.
4. List active package-owned workflows:

   ```bash
   temporal workflow list \
     --query 'ExecutionStatus = "Running" AND WorkflowType = "TemporalCommunity.Extensions.Agents.AgentWorkflow"'

   temporal workflow list \
     --query 'ExecutionStatus = "Running" AND WorkflowType = "TemporalCommunity.Extensions.Agents.AgentJobWorkflow"'
   ```

5. Gracefully shut down long-lived sessions through
   `ITemporalAgentClient.ShutdownAsync(sessionId)`. Allow finite `AgentJobWorkflow` runs to complete;
   cancel jobs whose application contract supports cancellation.
6. Terminate package-owned workflows that remain after the bounded drain period. Review the query
   output before using bulk termination:

   ```bash
   temporal workflow terminate \
     --query 'ExecutionStatus = "Running" AND WorkflowType = "TemporalCommunity.Extensions.Agents.AgentWorkflow"' \
     --reason "Pre-release agent worker cutover" \
     --yes

   temporal workflow terminate \
     --query 'ExecutionStatus = "Running" AND WorkflowType = "TemporalCommunity.Extensions.Agents.AgentJobWorkflow"' \
     --reason "Pre-release agent worker cutover" \
     --yes
   ```

7. Repeat both list commands and require **zero rows** before deploying corrected workers.
8. Drain or terminate application-owned workflows containing `TemporalAIAgent` by their actual
   application workflow types, then verify zero pre-change executions of those types remain.
9. Stop all old workers on affected task queues.
10. Deploy only corrected workers and matching clients. Verify every affected queue has the intended
    corrected worker set before reopening producers.
11. Unpause Schedules:

    ```bash
    temporal schedule toggle \
      --schedule-id "SCHEDULE_ID" \
      --unpause \
      --reason "Pre-release agent worker cutover complete"
    ```

12. Resume delayed/fire-and-forget starters and external callers. Start fresh sessions; do not reuse
    terminated workflow IDs unless the application's ID-reuse policy explicitly allows it.

## Verification and rollback

After deployment, verify that new sessions can run with all, subset, empty, and disabled per-turn
tool selections; blocked calls schedule no interceptor, approval, or tool activity; and operator
logs contain diagnostics without exposing the registered tool catalog to tenants.

If a regression requires rollback:

1. Stop producers and pause Schedules before terminating any corrected-version workflow.
2. Drain or terminate all corrected-version package-owned and containing workflows.
3. Verify zero corrected-version runs remain active.
4. Stop corrected workers.
5. Restore the complete old worker and client set.
6. Unpause Schedules and producers only after old workers are healthy.

Do not place old workers on histories created by corrected workflow code. A rollback starts fresh
old-version workflows after the corrected-version runs have been removed.
