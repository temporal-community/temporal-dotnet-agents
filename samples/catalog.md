# Sample Catalog

This is the authoritative index of runnable sample projects. Each entry is a tracked project root;
directories that contain only ignored build output are deliberately not samples. The verification
script checks that every `MAF`, `MEAI`, and `MCP` sample-project root appears exactly once.

"Automated" means the project is currently included in the local `just test-samples` sample-canary
command. "Manual" means it is not included in that command; consult the sample README or project for
its run procedure.

---

## 🟢 Level 1: Getting Started

Simple, single-process samples introducing core framework features.

| Sample | Stack | Intent | Execution Mode | Current run status |
| --- | --- | --- | --- | --- |
| [BasicAgent](MAF/BasicAgent/) | MAF | Start an agent worker and send a message from an external caller | Unattended Script | Automated: `just test-samples` |
| [ConfigurableAgent](MAF/ConfigurableAgent/) | MAF | Per-agent configuration and read-only tools | Unattended Script | Automated: `just test-samples` |
| [DurableChat](MEAI/DurableChat/) | MEAI | Multi-turn durable chat with worker-owned tools | Unattended Script | Automated: `just test-samples` |
| [DurableTools](MEAI/DurableTools/) | MEAI | Durable function dispatch and per-tool policies | Unattended Script | Automated: `just test-samples` |
| [DurableEmbeddings](MEAI/DurableEmbeddings/) | MEAI | Durable per-chunk embedding dispatch | Unattended Script | Automated: `just test-samples` |

---

## 🟡 Level 2: Core Patterns & Capabilities

Intermediate samples covering tool interceptors, context providers, approvals, and routing.

| Sample | Stack | Intent | Execution Mode | Current run status |
| --- | --- | --- | --- | --- |
| [AmbientAgent](MAF/AmbientAgent/) | MAF | Signal-driven ambient monitoring agent | Unattended Script | Automated: `just test-samples` |
| [ApprovalScopes](MAF/ApprovalScopes/) | MAF | Scope-aware approvals persisted across turns | Interactive Console | Manual |
| [ContextProviders](MAF/ContextProviders/) | MAF | Retry-safe custom `AIContextProvider` implementations | Unattended Script | Automated: `just test-samples` |
| [CustomWorkflow](MEAI/CustomWorkflow/) | MEAI | Extend the managed durable workflow with domain output | Unattended Script | Automated: `just test-samples` |
| [DirectAdapters](MEAI/DirectAdapters/) | MEAI | Hand-written Activity + `AsDurable()` for low-level custom workflows | Unattended Script | Manual |
| [DurableContextProvider](MAF/DurableContextProvider/) | MAF | Context-provided tools declared as durable activities | Unattended Script | Automated: `just test-samples` |
| [EvaluatorOptimizer](MAF/EvaluatorOptimizer/) | MAF | Generator and evaluator loop | Unattended Script | Automated: `just test-samples` |
| [ExtensibleDurableTurns](MEAI/ExtensibleDurableTurns/) | MEAI | Typed turn input/state with package-managed activities | Unattended Script | Automated: `just test-samples` |
| [HumanInTheLoop](MAF/HumanInTheLoop/) | MAF | Approval gates through workflow updates | Interactive Console | Manual |
| [HumanInTheLoop](MEAI/HumanInTheLoop/) | MEAI | Workflow-owned approval gates | Interactive Console | Automated: `just test-samples` |
| [MixedActivities](MAF/MixedActivities/) | MAF | Ordinary and AI activities in one workflow | Unattended Script | Automated: `just test-samples` |
| [PerToolActivities](MAF/PerToolActivities/) | MAF | Per-tool retry, timeout, and no-retry write policies | Unattended Script | Automated: `just test-samples` |
| [Skills](MAF/Skills/) | MAF | Progressive-disclosure skill catalog and durable loading | Unattended Script | Automated: `just test-samples` |
| [ToolInterceptor](MAF/ToolInterceptor/) | MAF | Proceed, pause, skip, or block before a tool executes | Unattended Script | Automated: `just test-samples` |
| [ToolInterceptor](MEAI/ToolInterceptor/) | MEAI | Intercept, pause, skip, or block tool calls | Unattended Script | Automated: `just test-samples` |

---

## 🔵 Level 3: Enterprise Architecture & MCP

Multi-process architectures, MCP tool servers, telemetry, and advanced security.

| Sample | Stack | Intent | Execution Mode | Current run status |
| --- | --- | --- | --- | --- |
| [McpTools](MAF/McpTools/) | MAF | Pinned or trusted MCP tools dispatched as durable activities | Unattended Script | Manual |
| [McpTools](MEAI/McpTools/) | MEAI | Pinned or trusted MCP tools as durable functions | Unattended Script | Manual |
| [MultiAgentRouting](MAF/MultiAgentRouting/) | MAF | Routing, parallel agent execution, and OpenTelemetry | Unattended Script | Automated: `just test-samples` |
| [OpenTelemetry](MEAI/OpenTelemetry/) | MEAI | OpenTelemetry traces, metrics, and usage ownership | Unattended Script | Automated: `just test-samples` |
| [PayloadCodec](MEAI/PayloadCodec/) | MEAI | Opt-in bounded gzip payload compression | Unattended Script | Manual |
| [SplitWorkerClient](MAF/SplitWorkerClient/) | MAF | Worker and client hosted in separate processes | Multi-Process | Manual |
| [WorkflowOrchestration](MAF/WorkflowOrchestration/) | MAF | Invoke sub-agents from a Temporal workflow | Unattended Script | Automated: `just test-samples` |
| [WorkflowRouting](MAF/WorkflowRouting/) | MAF | Static and dynamic durable routing | Unattended Script | Automated: `just test-samples` |
| [WorkflowToolServer](MCP/WorkflowToolServer/) | MCP | Authenticated MCP tools that start or join tenant-scoped workflows | Multi-Process | Manual |

---

## Choosing a sample

Choose the package first in the [Library Combinations Guide](../docs/library-combinations.md). Then use
this catalog to select a project by intent. Each sample owns its own prerequisites and production
limitations; do not transfer tool, history, or session assumptions between MAF and MEAI samples.
