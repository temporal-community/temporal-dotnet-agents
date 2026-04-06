---
name: integration_design_analysis
description: Four duplication points between Extensions.AI and Extensions.Agents reviewed in depth; design options ranked April 2026
type: project
---

Team completed a read-through of all four duplication points (HITL state machine, DataConverter auto-wiring, approval timeout logging, test serialization divergence) on branch investigation-meai.

**Why:** Preparing a structured design doc to decide which refactors to pursue before merging to main.

**How to apply:** Use as context when the team asks to implement any of the proposed options; prefer the ranked recommendations documented in the design doc over improvising.
