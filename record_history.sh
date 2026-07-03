@historian record
agent: sentinel
task: Thorough review of the TemporalAgents repository focusing on C# coding patterns, architectural conventions, and code quality. Analyze the MAF and MEAI integrations, use of modern C# 10 features, DI setup, workflow vs activity separation of concerns, and identify any anti-patterns, inconsistencies, or areas where the codebase diverges from idiomatic .NET or Temporal SDK best practices.
work: |
  - Analyzed the codebase for Temporal determinism violations, modern C# conventions, and DI setups.
  - Extracted positive patterns: Workflow/Activity separation is clearly respected; Determinism is mostly well-guarded (e.g., `Workflow.NewGuid()` preferred).
  - Identified a CRITICAL Temporal determinism bug in `AgentWorkflow.cs`: `GetRawText().GetHashCode()` is used for change-detection hash logic on `_lastSentStateBagHash`. String `GetHashCode()` is randomized per-process in .NET Core. Replaying this workflow after a restart or on a different worker will yield a different hash, changing the activity input from `null` to the full JSON, and triggering a `NonDeterministicWorkflowException`.
  - Identified modern C# areas for improvement: Replace `if (value <= 0) throw new ArgumentOutOfRangeException(...)` with .NET 8 `ArgumentOutOfRangeException.ThrowIfNegativeOrZero(...)` (e.g., `DurableChatToolOptions.cs`), adopt C# 12 collection expressions `[]` over `new List<T>()`.
files: src/TemporalCommunity.Extensions.Agents/Workflows/AgentWorkflow.cs, src/TemporalCommunity.Extensions.AI/DurableChatToolOptions.cs
decisions: Proposed an architectural pattern fix for hashing JSON elements deterministically and utilizing modern exception throw helpers.
next: The team should replace `GetHashCode()` in workflows with a deterministic alternative (e.g., `string.Compare`, deterministic hash, or SHA256) and update `laws.md` to ban `string.GetHashCode()` in workflows.
