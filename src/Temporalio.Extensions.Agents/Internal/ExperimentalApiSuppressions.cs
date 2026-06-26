// Auditable inventory of experimental base-library (MAF / MEAI) API dependencies.
//
// The global <NoWarn> in Directory.Build.props NO LONGER blankets MEAI001/MAAI001
// (the experimental-API gates for Microsoft.Extensions.AI and Microsoft.Agents.AI).
// Combined with <TreatWarningsAsErrors>true</TreatWarningsAsErrors>, that means any
// NEW consumption of an experimental base-library API fails the build until it is
// recorded. This file is the single greppable manifest of every experimental
// base-library surface this assembly depends on.
//
// IMPLEMENTATION NOTE — why this is a manifest, not a set of [assembly: SuppressMessage]:
//   MEAI001 / MAAI001 are emitted by the C# compiler from the [Experimental] attribute,
//   NOT by a Roslyn analyzer. The compiler honors `#pragma warning disable` and
//   project-level <NoWarn>, but it does NOT honor assembly-scoped [SuppressMessage] for
//   these diagnostics (verified: an assembly [SuppressMessage] leaves all 49 errors).
//   So suppression is applied at the only two levers that work:
//
//   1. MAAI001 (all occurrences are in OUR source) — file-level
//      `#pragma warning disable MAAI001` with a justification comment at the top of each
//      file that touches the experimental MAF surface. Scattered but auditable, and the
//      category stays LIVE in every other file (a new untouched experimental MAF API
//      still fails the build). Files:
//        - Skills/FileSkillsSource.cs   — AgentSkill, AgentSkillsSource, AgentSkillScript,
//                                          AgentSkillResource, AgentSkillFrontmatter,
//                                          AgentFileSkill, AgentFileSkillScriptRunner,
//                                          AgentFileSkillsSourceOptions
//        - Skills/SkillResolver.cs      — AgentSkill, AgentFileSkill (+ skills surface)
//        - Skills/SkillsBuilder.cs      — AgentSkillsSource, AgentFileSkill (+ skills surface)
//        - Workflows/AgentActivities.cs — AIContextProvider.InvokingContext /
//                                          AIContextProvider.InvokedContext ctors
//
//   2. MEAI001 (all occurrences are in GENERATED code only — the System.Text.Json
//      source generator over AgentSessionJsonContext/AgentResponse pulls in the
//      experimental ResponseContinuationToken (+ Converter) and
//      AgentResponse.ContinuationToken). Generated files cannot carry a `#pragma`, so
//      MEAI001 is suppressed via a documented, project-scoped <NoWarn> in
//      Temporalio.Extensions.Agents.csproj. No direct (hand-written) consumption of an
//      MEAI experimental API exists in this assembly; if one is added it will surface as
//      a normal MEAI001 error in source despite the generated-code NoWarn? No — <NoWarn>
//      is project-wide, so hand-written MEAI usage would also be silenced. This is the
//      one residual blanket; it is scoped to a single project and documented here so a
//      reviewer knows to grep for new MEAI usage when bumping the MEAI version.
//
// When a listed API graduates (loses [Experimental]) or is removed/renamed by Microsoft,
// the per-file pragma / NoWarn entry should be revisited. The base-contract guard tests
// (Wave 2) turn a removal/rename into a red CI test rather than a silent drift.
//
// See artifacts/research/codebase-remediation-plan.md §2.2 (S-F-1).
