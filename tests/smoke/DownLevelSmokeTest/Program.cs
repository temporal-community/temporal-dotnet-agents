// DOWN-LEVEL RUNTIME GATE — Trinity
// =================================
// Proves the netstandard2.1 assets of TemporalCommunity.Extensions.AI and
// .Agents actually RUN on .NET Core 3.1 (not just compile). Drives a minimal
// durable chat turn + one durable tool call end-to-end against an embedded
// Temporal dev server, modeled on the AI integration test
// DurableToolDispatchIntegrationTests.SingleToolCall_SingleTurn.
//
// By default it uses an inline scripted IChatClient so the DURABLE PATH is
// validated deterministically with no live LLM. This is the gate's canonical
// mode: it exercises the workflow sandbox, native core, activity dispatch, and
// JSON polymorphism on ns2.1 without a provider dependency.
//
// Exit code 0 = PASS, non-zero = FAIL (with a diagnostic message).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI;

namespace DownLevelSmokeTest
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            Console.WriteLine("=== Down-level (netstandard2.1 asset) runtime gate ===");
            Console.WriteLine("Runtime: {0}", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Console.WriteLine("Process arch: {0}", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            Console.WriteLine();

            try
            {
                // TargetFrameworkAttribute is the authoritative asset-selection signal: a
                // net8 proxy copies the selected DLL into its output directory, so its path is
                // not meaningful. This is a gate, not a diagnostic — fail closed if either
                // packed library did not resolve to the ns2.1 asset.
                AssertNetStandard21Asset(
                    typeof(DurableChatSessionClient).Assembly,
                    "TemporalCommunity.Extensions.AI");
                AssertNetStandard21Asset(
                    typeof(TemporalAgentsOptions).Assembly,
                    "TemporalCommunity.Extensions.Agents");

                await RunDurableChatWithToolAsync().ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine("=== GATE RESULT: PASS ===");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("=== GATE RESULT: FAIL ===");
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static void AssertNetStandard21Asset(System.Reflection.Assembly assembly, string libraryName)
        {
            var tfmAttr = assembly
                .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
                .Cast<System.Runtime.Versioning.TargetFrameworkAttribute>()
                .FirstOrDefault();

            Console.WriteLine("{0} loaded from:", libraryName);
            Console.WriteLine("  {0}", assembly.Location);
            Console.WriteLine("  compiled TargetFramework: {0}", tfmAttr?.FrameworkName ?? "(none)");
            if (tfmAttr?.FrameworkName?.Contains(".NETStandard,Version=v2.1") != true)
            {
                throw new InvalidOperationException(
                    libraryName + " did not resolve to its netstandard2.1 asset; refusing to report a passing down-level gate.");
            }
        }

        private static async Task RunDurableChatWithToolAsync()
        {
            Console.WriteLine("Starting embedded Temporal dev server (WorkflowEnvironment.StartLocalAsync)...");
#pragma warning disable CA2007 // Console smoke gate has no synchronization context; creation already configures its await.
            await using var env = await WorkflowEnvironment.StartLocalAsync().ConfigureAwait(false);
#pragma warning restore CA2007
            Console.WriteLine("Embedded server up.");

            // One durable tool: get_weather → "sunny, 72F".
            var toolInvoked = 0;
            var weatherTool = AIFunctionFactory.Create(
                new Func<string, object?>(city =>
                {
                    Interlocked.Increment(ref toolInvoked);
                    return (object?)"sunny, 72F";
                }),
                "get_weather",
                "Returns the current weather for a city.");

            // Scripted LLM: turn 1 asks for the tool, turn 2 gives the final answer.
            var scripted = new ScriptedChatClient(new[]
            {
                new ChatResponse(new ChatMessage(ChatRole.Assistant, new AIContent[]
                {
                    new FunctionCallContent(
                        "call-1",
                        "get_weather",
                        new Dictionary<string, object?> { ["city"] = "SF" }),
                })),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "The weather in SF is sunny, 72F.")),
            });

            // Separate script for the durable-agent path. This proves the Agents assembly is
            // loaded, registered, and executes an AgentWorkflow rather than merely appearing
            // as an unused PackageReference in the smoke project.
            var agentScripted = new ScriptedChatClient(new[]
            {
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Agent smoke response.")),
            });

            var taskQueue = "downlevel-smoke-" + Guid.NewGuid().ToString("N");

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<ITemporalClient>(env.Client);

            // Pattern 3 idiom: register the chat client WITHOUT UseFunctionInvocation().
            builder.Services
                .AddChatClient(scripted)
                .Build();

            // Stub embedding generator required by DurableEmbeddingActivities ctor injection.
            builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new NoopEmbeddingGenerator());

            var worker = builder.Services.AddHostedTemporalWorker(taskQueue);
            worker
                .AddDurableAI(opts =>
                {
                    opts.ActivityTimeout = TimeSpan.FromSeconds(60);
                    opts.HeartbeatTimeout = TimeSpan.FromSeconds(15);
                    opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
                })
                .AddDurableTools(weatherTool);

            worker.AddTemporalAgents(opts =>
            {
                // The standalone dev server starts without the custom search attributes that
                // production clusters register. Search-attribute behavior is covered by the
                // Agents integration suite; disable it here so this gate stays self-contained.
                opts.EnableSearchAttributes = false;
                opts.AddDurableAgent("smoke-agent", agent =>
                {
                    agent.Instructions = "Return the scripted smoke response.";
                    agent.ChatClient = _ => agentScripted;
                    agent.TimeToLive = TimeSpan.FromMinutes(5);
                });
            });

            using var host = builder.Build();
            Console.WriteLine("Starting worker host...");
            await host.StartAsync().ConfigureAwait(false);
            Console.WriteLine("Worker host started.");

            var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
            var conversationId = "smoke-" + Guid.NewGuid().ToString("N");

            Console.WriteLine("Sending durable chat turn ('What's the weather in SF?')...");
            var response = await sessionClient.SendAsync(
                conversationId,
                new[] { new ChatMessage(ChatRole.User, "What's the weather in SF?") }).ConfigureAwait(false);

            Console.WriteLine("Received response: \"{0}\"", response?.Text);
            Console.WriteLine("Tool invocation count: {0}", toolInvoked);

            // Assertions — mirror SingleToolCall_SingleTurn.
            if (response == null)
                throw new Exception("ASSERT FAILED: response was null.");
            if (string.IsNullOrEmpty(response.Text) || !response.Text!.Contains("sunny"))
                throw new Exception("ASSERT FAILED: response did not contain the tool result 'sunny'. Got: " + response.Text);
            if (toolInvoked != 1)
                throw new Exception("ASSERT FAILED: expected the durable tool to run exactly once; ran " + toolInvoked + " times.");

            Console.WriteLine("Sending a durable-agent turn...");
            var agentProxy = host.Services.GetTemporalAgentProxy("smoke-agent");
            var agentSession = await agentProxy.CreateSessionAsync().ConfigureAwait(false);
            var agentResponse = await agentProxy.RunAsync("Smoke test", agentSession).ConfigureAwait(false);
            if (string.IsNullOrEmpty(agentResponse.Text) || !agentResponse.Text!.Contains("Agent smoke response"))
                throw new Exception("ASSERT FAILED: durable agent returned an unexpected response.");

            if (agentSession is TemporalAgentSession temporalSession)
            {
                await host.Services.GetRequiredService<ITemporalAgentClient>()
                    .ShutdownAsync(temporalSession.SessionId)
                    .ConfigureAwait(false);
            }

            Console.WriteLine("Assertions passed: durable chat + tool and durable-agent paths succeeded on ns2.1.");

            await host.StopAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Minimal scripted chat client: returns a pre-defined sequence of responses.
        /// Inlined here (not the test-helper ScriptedChatClient) because this is a
        /// packed-consumer project with no reference to the test assemblies.
        /// </summary>
        private sealed class ScriptedChatClient : IChatClient
        {
            private readonly Queue<ChatResponse> _scripted;

            public ScriptedChatClient(IEnumerable<ChatResponse> scriptedResponses)
            {
                _scripted = new Queue<ChatResponse>(scriptedResponses);
            }

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                if (_scripted.Count == 0)
                    throw new InvalidOperationException("ScriptedChatClient ran out of scripted responses.");
                return Task.FromResult(_scripted.Dequeue());
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                foreach (var update in response.ToChatResponseUpdates())
                {
                    yield return update;
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() { }
        }

        /// <summary>Stub embedding generator; satisfies DurableEmbeddingActivities ctor injection.</summary>
        private sealed class NoopEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
        {
            public EmbeddingGeneratorMetadata Metadata { get; } = new EmbeddingGeneratorMetadata("noop", null, null, 1);

            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
                IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                    values.Select(_ => new Embedding<float>(new[] { 0f })).ToList()));

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }
}
