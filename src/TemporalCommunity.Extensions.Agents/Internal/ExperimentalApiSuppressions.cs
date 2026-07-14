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
//        - Skills/SkillResolver.cs           — AgentSkill, AgentSkillsSourceContext (+ skills surface)
//        - Skills/SkillsBuilder.cs           — AgentSkillsSource, AgentFileSkillsSource,
//                                              AgentFileSkillScriptRunner, AgentFileSkillsSourceOptions
//        - Skills/SkillsContextProvider.cs   — AgentSkillsSourceContext (passed to EnsureLoadedAsync)
//        - Workflows/AgentActivities.cs      — AIContextProvider.InvokingContext /
//                                              AIContextProvider.InvokedContext ctors
//
//   2. MEAI001 (all occurrences are in GENERATED code only — the System.Text.Json
//      source generator over AgentSessionJsonContext/AgentResponse pulls in the
//      experimental ResponseContinuationToken (+ Converter) and
//      AgentResponse.ContinuationToken). Generated files cannot carry a `#pragma`, so
//      MEAI001 is suppressed via a documented, project-scoped <NoWarn> in
//      TemporalCommunity.Extensions.Agents.csproj. No direct (hand-written) consumption of an
//      MEAI experimental API exists in this assembly today. Because the <NoWarn> is
//      project-wide, any hand-written MEAI usage added later would also be silenced (not
//      surfaced as an MEAI001 error). This is the one residual blanket; it is scoped to a
//      single project and documented here so a reviewer knows to grep for new MEAI usage
//      when bumping the MEAI version.
//
//      NOTE (MEAI 10.7.0): AllowBackgroundResponses on ChatOptions graduated out of
//      [Experimental] in MEAI 10.7.0. The narrowly-scoped MEAI001 pragma pair in
//      TemporalCommunity.Extensions.AI/DurableChatClient.cs was dropped at that bump.
//
// When a listed API graduates (loses [Experimental]) or is removed/renamed by Microsoft,
// the per-file pragma / NoWarn entry should be revisited. The base-contract guard tests
// (Wave 2) turn a removal/rename into a red CI test rather than a silent drift.
//
// Contract-guard tests turn upstream experimental API drift into a visible CI failure.
