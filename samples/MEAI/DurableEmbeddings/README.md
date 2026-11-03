# DurableEmbeddings: Application-Owned Activity

This sample demonstrates calling an application-owned Temporal activity that uses MEAI's
`IEmbeddingGenerator<string, Embedding<float>>`. The Temporal activity timeout and retry policy are
configured by the workflow; the activity forwards its cancellation token to the generator.

Activity inputs and vector results are stored in Temporal history. For large or sensitive vectors,
store the vector externally and return a reference; make external writes idempotent because retries
may repeat an incomplete activity attempt.

## Prerequisites

- .NET 10 SDK
- Temporal Service 1.31.0 or newer
- `OPENAI_API_KEY` and `OPENAI_API_BASE_URL`
- Optional `OPENAI_EMBEDDING_MODEL` (defaults to `text-embedding-3-small`)

Use user secrets in Development or set the values in the environment:

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableEmbeddings
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableEmbeddings
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj
```

See the [MEAI embeddings guide](../../../docs/how-to/MEAI/embeddings.md) for the code pattern and
payload/privacy considerations.
