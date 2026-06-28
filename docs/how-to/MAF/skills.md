# Skills (`UseSkills` / `SkillsBuilder`)

A structured way to give an agent access to a catalog of prompt instructions without loading every instruction into the context window on every turn. The agent receives a compact index listing skill names and descriptions, then fetches the full content of a specific skill only when needed.

---

## Table of Contents

1. [What skills are](#what-skills-are)
2. [Why use skills](#why-use-skills)
3. [Quick start](#quick-start)
4. [SKILL.md format](#skillmd-format)
5. [SkillsBuilder API](#skillsbuilder-api)
6. [Tools registered by UseSkills](#tools-registered-by-useskills)
7. [SkillsContextProvider and the skill index](#skillscontextprovider-and-the-skill-index)
8. [Inline and class-based skills](#inline-and-class-based-skills)
9. [Script execution (opt-in)](#script-execution-opt-in)
10. [File-skill drift](#file-skill-drift)
11. [What is not supported](#what-is-not-supported)

---

## What skills are

A _skill_ in MAF terms is a named prompt template or instruction set that an agent can load on demand. MAF ships three concrete subtypes:

| Subtype | How you supply the content |
|---|---|
| `AgentFileSkill` | A `SKILL.md` file on disk — `UseSkills` creates these via `AddSkillsFromDirectory` |
| `AgentInlineSkill` | An instance created from code with `instructions` passed directly |
| `AgentClassSkill<TSelf>` | A class that overrides MAF's base to provide content, resources, and scripts |

All three are registered through `UseSkills(Action<SkillsBuilder>)` on `DurableAgentBuilder`. The library treats them uniformly: the same compact index, the same `load_skill` tool, the same durable activity boundary.

---

## Why use skills

- **Large prompt libraries, small context windows.** Injecting every system prompt on every LLM call wastes tokens. With skills, only the index (~100 tokens per skill) is injected; full content arrives only when the agent explicitly calls `load_skill`.
- **Dynamic instructions.** Skills are loaded on demand from the resolver; the agent can discover and load different instruction sets within a single session.
- **Observability.** Each `load_skill`, `read_skill_resource`, and `run_skill_script` call is a separate `InvokeAgentTool` activity visible in the Temporal Web UI, with its own retry and timeout guarantees.

---

## Quick start

The example below registers a `SupportAgent` that scans a `./skills` directory for `SKILL.md` files.

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("SupportAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.Instructions = "You are a helpful support agent. " +
                "A catalog of skills is available to you. " +
                "Use load_skill to fetch instructions for a relevant skill before proceeding.";

            // Scan ./skills and all subdirectories up to 2 levels deep.
            agent.UseSkills(s =>
            {
                s.AddSkillsFromDirectory("./skills");
            });
        });
    });
```

On every LLM call the agent receives a system message containing a compact XML index of all discovered skills. When the agent calls `load_skill("expense-report")`, the library fetches and returns the full `SKILL.md` content as a `InvokeAgentTool` activity.

---

## SKILL.md format

A `SKILL.md` file is a Markdown document with a YAML frontmatter block. The scanner looks for files named exactly `SKILL.md` (case-sensitive on Linux).

```markdown
---
name: expense-report
description: File and track employee expense reports using the Concur API.
license: MIT
compatibility: v2
---

## Expense Report Skill

Use this skill to help employees submit, track, and correct expense reports.

### Prerequisites
- Employee must be logged in with SSO.
- Report manager must be set in HR system.

### Steps
1. Collect itemized receipts and amounts.
2. Call `submit_expense_report` with the collected data.
3. Record the report ID returned and confirm with the user.
```

### Frontmatter rules

| Field | Required | Notes |
|---|---|---|
| `name` | Yes | Kebab-case identifier. Used by `load_skill` and the skill index. |
| `description` | Yes | Shown in the compact index every LLM call sees. |
| `license` | No | Stored on the skill; not shown in the index. |
| `compatibility` | No | Stored on the skill; not shown in the index. |

**Values must be unquoted strings.** A line like `name: "expense-report"` includes the quote characters verbatim, which will fail MAF name validation and cause the skill to be silently skipped.

**The first line of the file must be `---`** — no leading blank lines. A BOM at the start of the file is stripped automatically, but any other whitespace before the opening delimiter causes a parse failure.

**Malformed files are silently skipped** (logged as warnings) unless you construct a `FileSkillsSource` directly and pass a logger:

```csharp
using TemporalCommunity.Extensions.Agents.Skills;

agent.UseSkills(s =>
{
    // Pass a FileSkillsSource directly to get warning logs for malformed files.
    s.AddSkillsSource(new FileSkillsSource(
        "./skills",
        maxDepth: 2,
        logger: loggerFactory.CreateLogger<FileSkillsSource>()));
});
```

---

## SkillsBuilder API

```csharp
agent.UseSkills(s =>
{
    // File-based: scans up to 2 levels deep (root + children + grandchildren).
    s.AddSkillsFromDirectory("./skills");

    // File-based with explicit depth: 0 = root only, 1 = root + children.
    // AddSkillsFromDirectory does not expose maxDepth — use FileSkillsSource directly.
    s.AddSkillsSource(new FileSkillsSource("./core-skills", maxDepth: 0));

    // Inline: single skill defined in code.
    s.AddSkill(new AgentInlineSkill(
        name: "quick-reply",
        description: "Compose short, empathetic customer replies.",
        instructions: "## Quick Reply\nKeep replies under 3 sentences. Start with acknowledgment."));

    // Inline: multiple skills from an enumerable.
    s.AddSkills(GetSkillsFromDatabase());

    // Custom: bring your own AgentSkillsSource subclass.
    s.AddSkillsSource(new RemoteSkillsSource(catalogClient));

    // Opt in to run_skill_script (see Script execution below).
    s.EnableScriptExecution();
});
```

### AddSkillsFromDirectory depth semantics

`maxDepth` controls how many directory levels the scanner descends:

| `maxDepth` | Directories scanned |
|---|---|
| `0` | Root directory only |
| `1` | Root + immediate subdirectories |
| `2` (default) | Root + two levels (root, children, grandchildren) |

Skills discovered across all directories are sorted by name (`OrdinalIgnoreCase`) before the catalog is built, so the order of `SKILL.md` files on disk does not affect the order the agent sees.

### Duplicate skill names

If two skills share the same name (case-insensitive), `SkillResolver` throws `InvalidOperationException` when the catalog is first materialized. This fires inside the first `InvokeAgentTool` or `ProvideAIContextAsync` call on the worker, not at registration time.

---

## Tools registered by UseSkills

Calling `UseSkills(...)` registers the following tools automatically. You do not need to call `AddTool` manually.

| Tool | Description | `SkipInterceptor` | `RequireApproval` | `NoRetry` |
|---|---|---|---|---|
| `load_skill` | Returns full skill content by name | Yes | — | — |
| `read_skill_resource` | Returns a named resource from a skill | — | — | — |
| `run_skill_script` | Runs a named script from a skill | — | Yes (always) | Yes |

**`load_skill`** — read-only with no side effects; the interceptor is skipped for it. Returns the raw `SKILL.md` content for file-based skills, or synthesized XML for inline and class-based skills. When script execution is disabled, any `<scripts>` block is stripped from synthesized XML before returning.

**`read_skill_resource`** — can delegate to resource implementations that perform I/O, so the interceptor fires by default. Returns the resource content string; returns a "not found" message for unknown skills or resource names.

**`run_skill_script`** — only registered when `EnableScriptExecution()` is called. Always requires human approval before dispatching (the `RequireApproval()` floor applies unconditionally — the interceptor cannot override it to `Proceed`). Set `NoRetry` because script execution is a write-style operation.

---

## SkillsContextProvider and the skill index

`UseSkills` automatically registers a `SkillsContextProvider` as an `AIContextProvider` on the agent. You do not register it separately.

On the first LLM call of a session, the provider:

1. Calls `SkillResolver.EnsureLoadedAsync` to materialize the skill catalog (scanning directories if needed).
2. Builds a compact XML index listing name and description for every skill.
3. Injects the index as a `ChatRole.System` message into the LLM call.
4. Persists the index text to `AgentSessionStateBag["temporal.skills_index"]`.

On subsequent LLM calls the cached index is read from the StateBag directly — no rescanning.

**Index format** (approximately 100 tokens per skill):

```xml
<skills>
  <skill><name>expense-report</name><description>File and track employee expense reports using the Concur API.</description></skill>
  <skill><name>quick-reply</name><description>Compose short, empathetic customer replies.</description></skill>
</skills>
```

Because the index is cached in the StateBag, it survives continue-as-new transitions. When the worker restarts and a new `SkillResolver` instance is created, the StateBag-cached index is replayed into the provider on the next turn — no directory rescan until the StateBag itself is absent (for example, a brand new session).

**StateBag size.** The index accumulates in `AgentSessionStateBag`, which is subject to the 64 KB carry-forward warning. Keep the registered skill count reasonable (typically fewer than 50) to avoid bloat.

---

## Inline and class-based skills

For skills whose content you define in code rather than on disk, use `AgentInlineSkill` or a `AgentClassSkill<TSelf>` subclass:

```csharp
agent.UseSkills(s =>
{
    // Inline skill with a resource attached.
    var skill = new AgentInlineSkill(
        name: "invoice-lookup",
        description: "Look up invoice status and payment history.",
        instructions: """
            ## Invoice Lookup Skill

            Use the provided tools to retrieve invoice status.
            Escalate to a human if the status is DISPUTED.
            """);

    skill.AddResource(
        name: "status-codes",
        description: "Canonical invoice status code definitions.",
        method: async (sp, ct) =>
        {
            var db = sp.GetRequiredService<InvoiceDb>();
            return await db.GetStatusCodeReferenceAsync(ct);
        });

    s.AddSkill(skill);
});
```

For inline and class-based skills, `load_skill` returns synthesized XML rather than raw Markdown. The XML format is defined by MAF's `AgentInlineSkill.Content` property.

---

## Script execution (opt-in)

Script execution is disabled by default. To enable it, call `EnableScriptExecution()` on the builder:

```csharp
agent.UseSkills(s =>
{
    var skill = new AgentInlineSkill(
        name: "data-export",
        description: "Export session data to a reporting system.",
        instructions: "## Data Export\nUse run_skill_script to trigger exports.");

    skill.AddScript(
        name: "export-session",
        description: "Trigger a report export for the current session.",
        method: async (sp, args, ct) =>
        {
            var svc = sp.GetRequiredService<ReportingService>();
            var sessionId = args.TryGetValue("sessionId", out var v) ? v?.ToString() : null;
            await svc.ExportAsync(sessionId, ct);
            return "Export triggered.";
        });

    s.AddSkill(skill);
    s.EnableScriptExecution();  // registers run_skill_script with RequireApproval gate
});
```

When `EnableScriptExecution()` is called:

- The `run_skill_script` tool is registered with `RequireApproval()` and `NoRetry()`.
- The LLM can call `run_skill_script(skillName, scriptName, argumentsJson)`.
- Before the tool activity dispatches, the HITL approval flow pauses the workflow and waits for a human to approve via `SubmitApprovalAsync`. For the full approval API, see [HITL Patterns](./hitl-patterns.md).

**File-backed scripts are not supported.** `AddSkillsFromDirectory` throws `NotSupportedException` if you pass a `runner` argument. Script execution is available only for inline skills (`AgentInlineSkill`), class-based skills (`AgentClassSkill<TSelf>`), or a custom `AgentSkillsSource`.

---

## File-skill drift

`SkillResolver` materializes from file sources once — on the first call to `EnsureLoadedAsync` after a worker start. After a worker restart or a continue-as-new transition, the resolver is recreated and will re-scan the directories on its next use.

**If the directory contents change between turns**, the resolver will reflect the new state after the restart, but the StateBag still holds the old index text. The agent's compact index may advertise skills that no longer exist on disk, or miss newly added ones, until the StateBag entry is refreshed.

Treat file skill directories as effectively immutable for the lifetime of a session. If you must update the catalog mid-session, ensure the new session starts fresh (no carried StateBag) or clear `AgentSessionStateBag["temporal.skills_index"]` externally between sessions.

---

## What is not supported

| Unsupported | Notes |
|---|---|
| `AgentFileSkillsSourceOptions` (extension filters, custom depth via the options type) | Use `FileSkillsSource` directly with `maxDepth` for depth control |
| File-backed script runners (`AddSkillsFromDirectory` with `runner:` argument) | Throws `NotSupportedException` — use inline or class-based skills |
| `AgentFileSkill.Resources` via the file scanner | `null` is passed to the constructor; no resource delegates are loaded from disk |
| `AgentFileSkill.Scripts` via the file scanner | `null` is passed to the constructor; scripts in SKILL.md are not executed |

For skills that need resources or scripts, use `AgentInlineSkill` or a custom `AgentSkillsSource` registered via `AddSkillsSource`.

---

## See also

- [Individual MAF Context Providers](./individual-context-providers.md) — `TodoProvider`, `AgentModeProvider`, and other MAF `AIContextProvider` subclasses compatible with `AddContextProvider`
- [Tool Interceptor](./tool-interceptor.md) — apply policy before `InvokeAgentTool` activities, including those dispatched by skill tools
- [HITL Patterns](./hitl-patterns.md) — approval dashboard API used by `run_skill_script`
- [Testing Agents](./testing-agents.md) — unit-testing `UseSkills` registration with `DurableAgentBuilder` and `StubChatClient`
- [Durable Agents](./durable-agents.md) — per-tool retry and timeout configuration; `SkipInterceptor`, `RequireApproval`, `NoRetry` reference

---

_Last updated: 2026-06-03_
