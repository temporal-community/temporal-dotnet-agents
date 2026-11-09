# Evaluator-Optimizer through Activities

This sample composes registered Generator and Evaluator agents in a regular Temporal workflow.
Each turn is invoked by a scoped activity with its own stable session and request IDs. The workflow
uses the evaluator's response to decide whether to stop or ask the Generator for another revision.

## Run

Start Temporal with the required search attributes and configure `OPENAI_API_KEY` and
`OPENAI_API_BASE_URL`:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/EvaluatorOptimizer/EvaluatorOptimizer.csproj
```
