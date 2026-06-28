// DurableToolInterceptorResult and DurableToolOutcome were previously defined here as
// Agents-library-internal types. They have been consolidated into TemporalCommunity.Extensions.AI
// so that both the MAF and MEAI paths share a single DTO definition.
//
// The global-using directives below redirect the unqualified names in all Agents-library
// files to the canonical AI-library types, preserving compilation without widespread renames.

global using DurableToolInterceptorResult = TemporalCommunity.Extensions.AI.Tools.DurableToolInterceptorResult;
global using DurableToolOutcome = TemporalCommunity.Extensions.AI.Tools.DurableToolOutcome;
