# Temporal .NET Agents Documentation

Welcome to the documentation for **Temporal .NET Agents**, providing durable execution for AI agent workflows and LLM chat sessions using the Temporal .NET SDK.

---

## 📚 Guides by Library

### Microsoft Agent Framework (MAF Integration)
*`TemporalCommunity.Extensions.Agents`*

- **[Usage & Getting Started](how-to/MAF/usage.md)** — Registering durable agents, options, and hosting setup.
- **[Durable Agents & Tools](how-to/MAF/durable-agents.md)** — Activity-backed tool execution, retry policies, and timeouts.
- **[Routing & Agent Orchestration](how-to/MAF/routing.md)** — Multi-agent routing, workflow delegation, and sub-agents.
- **[Human-in-the-Loop (HITL)](how-to/MAF/hitl-patterns.md)** — Workflow approval gates, session approval scopes, and interactive reviews.
- **[Context Providers & Memory](how-to/MAF/individual-context-providers.md)** — Session memory, state bags, working set providers, and dynamic prompt injection.
- **[Skills & Toolsets](how-to/MAF/skills.md)** — Progressive skill loading and filesystem/inline skills.
- **[Tool Interceptors](how-to/MAF/tool-interceptor.md)** — Intercepting, auditing, and gating tool invocations.
- **[LLM Call Interception](how-to/MAF/llm-call-interception.md)** — Pipeline middleware and agent decorators (`AIAgentBuilder`).
- **[Observability & OpenTelemetry](how-to/MAF/observability.md)** — Distributed tracing, metrics, and activity sources.
- **[Testing Agents](how-to/MAF/testing-agents.md)** — Unit testing and integration testing patterns.
- **[Scheduling & Delayed Runs](how-to/MAF/scheduling.md)** — Delayed agent execution and background scheduling.
- **[Structured Output](how-to/MAF/structured-output.md)** — Schema-enforced responses and JSON output.
- **[Prompt Caching & Token Optimization](how-to/MAF/prompt-caching.md)** — History compaction, truncation, and token budgeting.
- **[Do's and Don'ts](how-to/MAF/dos-and-donts.md)** — Best practices for deterministic workflow development.

### Microsoft.Extensions.AI (MEAI Integration)
*`TemporalCommunity.Extensions.AI`*

- **[Usage & Getting Started](how-to/MEAI/usage.md)** — Setting up `DurableChatSessionClient` and `AddDurableAI`.
- **[Durable Tools](how-to/MEAI/tool-functions.md)** — Converting `AIFunction` into durable activities (`AsDurable`).
- **[Human-in-the-Loop (HITL)](how-to/MEAI/hitl-patterns.md)** — Approval requests, signals, and workflow updates.
- **[Embeddings](how-to/MEAI/embeddings.md)** — Durable embedding generation with activity retries and backoff.
- **[Payload Codecs & Encryption](how-to/MEAI/payload-codecs.md)** — Custom data converters, encryption, and compression.
- **[Custom Workflow Output](how-to/MEAI/custom-workflow-output.md)** — Processing intermediate turn data and structured outputs.
- **[Observability](how-to/MEAI/observability.md)** — OpenTelemetry integration for MEAI chat pipelines.
- **[Testing](how-to/MEAI/testing.md)** — Mocking chat clients, testing workflows, and local test environments.

---

## 🏛️ Architecture & Deep Dives

- **[Durability & Determinism](architecture/MAF/durability-and-determinism.md)** — Replay safety, workflow state, and activity boundaries.
- **[Agent Sessions & Workflow Loop](architecture/MAF/agent-sessions-and-workflow-loop.md)** — How agent workflows execute and maintain state.
- **[Durable Chat Pipeline](architecture/MEAI/durable-chat-pipeline.md)** — Turn lifecycle and state management.
- **[Cross-Library Integration](architecture/MEAI/cross-library-integration.md)** — How MEAI and MAF integrations fit together.
- **[Session StateBag & Context Providers](architecture/MAF/session-statebag-and-context-providers.md)** — State storage across continue-as-new transitions.
- **[Agent-to-Agent Communication](architecture/MAF/agent-to-agent-communication.md)** — Event-driven and direct sub-agent invocation.

---

## 🚀 Navigation & Resources

- **[Library Combinations Guide](library-combinations.md)** — Choosing between MEAI and MAF integrations.
- **[Sample Catalog](../samples/catalog.md)** — Complete catalog of runnable samples.
- **[Security Policy](security.md)** — Security considerations and disclosures.
