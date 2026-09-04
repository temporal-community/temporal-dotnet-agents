# Sample Catalog

This is the authoritative index of runnable sample projects. Each entry is a tracked project root;
directories that contain only ignored build output are deliberately not samples. The verification
script checks that every `MAF`, `MEAI`, and `MCP` sample-project root appears exactly once.

"Automated" means the project is currently included in the named local sample-canary recipe. "Manual"
means it is not included in that recipe; consult the sample README or project for its run procedure.

## `TemporalCommunity.Extensions.Agents` (MAF)

| Sample | Intent | Current run status |
| --- | --- | --- |
| [AmbientAgent](MAF/AmbientAgent/) | Signal-driven ambient monitoring agent | Automated: `just test-samples-maf` |
| [ApprovalScopes](MAF/ApprovalScopes/) | Scope-aware approvals persisted across turns | Manual |
| [BasicAgent](MAF/BasicAgent/) | Start an agent worker and send a message from an external caller | Automated: `just test-samples-maf` |
| [ConfigurableAgent](MAF/ConfigurableAgent/) | Per-agent configuration and read-only tools | Automated: `just test-samples-maf` |
| [ContextProviders](MAF/ContextProviders/) | Retry-safe custom `AIContextProvider` implementations | Automated: `just test-samples-maf` |
| [DurableContextProvider](MAF/DurableContextProvider/) | Context-provided tools declared as durable activities | Automated: `just test-samples-maf` |
| [EvaluatorOptimizer](MAF/EvaluatorOptimizer/) | Generator and evaluator loop | Automated: `just test-samples-maf` |
| [HumanInTheLoop](MAF/HumanInTheLoop/) | Approval gates through workflow updates | Manual |
| [McpTools](MAF/McpTools/) | Pinned or trusted MCP tools dispatched as durable activities | Manual |
| [MixedActivities](MAF/MixedActivities/) | Ordinary and AI activities in one workflow | Automated: `just test-samples-maf` |
| [MultiAgentRouting](MAF/MultiAgentRouting/) | Routing, parallel agent execution, and OpenTelemetry | Automated: `just test-samples-maf` |
| [PerToolActivities](MAF/PerToolActivities/) | Per-tool retry, timeout, and no-retry write policies | Automated: `just test-samples-maf` |
| [Skills](MAF/Skills/) | Progressive-disclosure skill catalog and durable loading | Automated: `just test-samples-maf` |
| [SplitWorkerClient](MAF/SplitWorkerClient/) | Worker and client hosted in separate processes | Manual |
| [ToolInterceptor](MAF/ToolInterceptor/) | Proceed, pause, skip, or block before a tool executes | Automated: `just test-samples-maf` |
| [WorkflowOrchestration](MAF/WorkflowOrchestration/) | Invoke sub-agents from a Temporal workflow | Automated: `just test-samples-maf` |
| [WorkflowRouting](MAF/WorkflowRouting/) | Static and dynamic durable routing | Automated: `just test-samples-maf` |
| [WorkingSet](MAF/WorkingSet/) | Stateful working-set context without provider-owned history | Automated: `just test-samples-maf` |

## `TemporalCommunity.Extensions.AI` (MEAI)

| Sample | Intent | Current run status |
| --- | --- | --- |
| [CustomWorkflow](MEAI/CustomWorkflow/) | Extend the managed durable workflow with domain output | Automated: `just test-samples-meai` |
| [DurableChat](MEAI/DurableChat/) | Multi-turn durable chat with worker-owned tools | Automated: `just test-samples-meai` |
| [DurableEmbeddings](MEAI/DurableEmbeddings/) | Durable per-chunk embedding dispatch | Automated: `just test-samples-meai` |
| [DurableTools](MEAI/DurableTools/) | Durable function dispatch and per-tool policies | Automated: `just test-samples-meai` |
| [ExtensibleDurableTurns](MEAI/ExtensibleDurableTurns/) | Typed turn input/state with package-managed activities | Automated: `just test-samples-meai` |
| [HumanInTheLoop](MEAI/HumanInTheLoop/) | Workflow-owned approval gates | Automated: `just test-samples-meai` |
| [McpTools](MEAI/McpTools/) | Pinned or trusted MCP tools as durable functions | Manual |
| [OpenTelemetry](MEAI/OpenTelemetry/) | OpenTelemetry traces, metrics, and usage ownership | Automated: `just test-samples-meai` |
| [PayloadCodec](MEAI/PayloadCodec/) | Opt-in bounded gzip payload compression | Manual |
| [ToolInterceptor](MEAI/ToolInterceptor/) | Intercept, pause, skip, or block tool calls | Automated: `just test-samples-meai` |

## MCP server composition

| Sample | Intent | Current run status |
| --- | --- | --- |
| [WorkflowToolServer](MCP/WorkflowToolServer/) | Authenticated MCP tools that start or join tenant-scoped workflows | Manual |

## Choosing a sample

Choose the package first in the [Library Combinations Guide](../docs/library-combinations.md). Then use
this catalog to select a project by intent. Each sample owns its own prerequisites and production
limitations; do not transfer tool, history, or session assumptions between MAF and MEAI samples.
