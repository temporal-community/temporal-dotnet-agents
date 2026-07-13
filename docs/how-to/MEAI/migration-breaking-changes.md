# MEAI durable-chat breaking changes

This prerelease change retires the inline durable-chat tool path. There is no compatibility mode.

Before deploying the new worker set, drain or explicitly retire any in-flight durable-chat
workflows created with the previous behavior. Do not rely on replaying those executions under the
new model/tool command sequence.

## Required application changes

1. Remove `UseFunctionInvocation()` from the `IChatClient` pipeline used by
   `DurableChatSessionClient`.
2. Register every tool with `AddDurableTools(...)` on the Temporal worker.
3. Remove every `ChatOptions.Tools` assignment passed to `DurableChatSessionClient.SendAsync`.
4. Ensure each worker serving the task queue registers the same stable tool names and compatible
   function schemas.
5. Replace `IDurableChatSessionClient.SubmitApprovalAsync(...)` with
   `ResolveApprovalAsync(...)` and handle its retry-safe result. Identical retries return
   `AlreadyResolved`; a conflicting retry returns `Conflict`.

`ChatOptions.Tools` now throws `DurableConfigurationException` at the durable-session boundary.
The workflow obtains schemas from `DurableFunctionRegistry` and schedules returned tool calls as
individual `InvokeFunction` activities.

`AIFunction.AsDurable()` remains available for a custom workflow that explicitly invokes a known
function. It is independent of the managed chat-session loop and does not make caller-provided
`ChatOptions.Tools` supported.
