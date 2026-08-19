# Schema fingerprint v1 contract

`DurableJsonSchemaFingerprint` is a persisted deployment-drift contract used by durable tool
declarations and manifests. Its successful output is frozen for version 1.

## Canonical form

- Object properties are sorted with `StringComparer.Ordinal` at every nesting level.
- Array order is preserved.
- Strings, booleans, and null use `Utf8JsonWriter` JSON encoding.
- Numbers representable as `decimal` retain the writer's representation, including decimal scale.
- Other finite `double` values use the writer's numeric representation.
- A syntactically valid JSON number outside those finite ranges is written from its validated raw JSON
  token. It is never converted to an infinity token.
- Duplicate object properties and unsupported `JsonValueKind` values are configuration failures.
- SHA-256 is encoded as lowercase hexadecimal.

Consequently, `1`, `1.0`, and `1.00` intentionally have different version-1 fingerprints. Whitespace
and object-property order do not affect a fingerprint; array order and numeric spelling can.

## Failure behavior

Registration-time invalid declarations throw `DurableConfigurationException`. Once declaration or
manifest data is part of a workflow input/history, expected configuration failures are surfaced as a
stable non-retryable Temporal `ApplicationFailureException`. They must not escape as ordinary workflow
exceptions, because that would repeatedly fail the Workflow Task.

The fingerprint detects accidental schema/policy drift. It does not authenticate a worker, authorize a
tool call, or protect data from tampering by a party that controls workflow payloads.

## Evolution

Changing any successful version-1 output requires a new fingerprint version, fixed test vectors, old
and new history replay, and a Worker Versioning or drain/cutover plan. Do not silently "improve"
normalization in place.
