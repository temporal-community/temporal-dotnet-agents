using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class DurableToolActivationPrototypeTests
{
    [Fact]
    public void DeclarationSnapshot_ReconstructsDeclarationWithoutImplementation()
    {
        var implementation = AIFunctionFactory.Create(
            (string value) => value.Length,
            new AIFunctionFactoryOptions { Name = "measure", Description = "Measures text." });

        var snapshot = DurableFunctionDeclarationSnapshot.Create(implementation.AsDeclarationOnly());
        var declaration = snapshot.ToDeclaration();

        Assert.IsAssignableFrom<AIFunctionDeclaration>(declaration);
        Assert.False(declaration is AIFunction);
        Assert.Equal(implementation.Name, declaration.Name);
        Assert.Equal(implementation.Description, declaration.Description);
        Assert.Equal(snapshot.JsonSchemaFingerprint, DurableJsonSchemaFingerprint.Create(declaration.JsonSchema));
        Assert.Equal(
            snapshot.ReturnJsonSchemaFingerprint,
            DurableJsonSchemaFingerprint.Create(declaration.ReturnJsonSchema!.Value));
    }

    [Fact]
    public void DeclarationSnapshot_RejectsAdditionalPropertiesWithSortedKeys()
    {
        var function = AIFunctionFactory.Create(
            () => "ok",
            new AIFunctionFactoryOptions
            {
                Name = "configured",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["zeta"] = new object(),
                    ["alpha"] = 1,
                },
            });

        var exception = Assert.Throws<DurableConfigurationException>(
            () => DurableFunctionDeclarationSnapshot.Create(function.AsDeclarationOnly()));

        Assert.Contains("configured", exception.Message, StringComparison.Ordinal);
        Assert.Contains("alpha, zeta", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateImplementation_RejectsPropertiesBeforeInvocation()
    {
        var declaration = AIFunctionFactory.Create(() => "declared", "tool");
        var invocationCount = 0;
        var implementation = AIFunctionFactory.Create(
            () =>
            {
                invocationCount++;
                return "implemented";
            },
            new AIFunctionFactoryOptions
            {
                Name = "tool",
                AdditionalProperties = new Dictionary<string, object?> { ["unsupported"] = true },
            });

        var snapshot = DurableFunctionDeclarationSnapshot.Create(declaration.AsDeclarationOnly());
        var exception = Assert.Throws<ApplicationFailureException>(
            () => snapshot.ValidateImplementation(implementation));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public void ValidateImplementation_RejectsNameAndSchemaMismatchNonRetryably()
    {
        var declared = AIFunctionFactory.Create((string value) => value, "declared");
        var implementation = AIFunctionFactory.Create((int value) => value, "implemented");
        var snapshot = DurableFunctionDeclarationSnapshot.Create(declared.AsDeclarationOnly());

        var exception = Assert.Throws<ApplicationFailureException>(
            () => snapshot.ValidateImplementation(implementation));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
    }

    [Fact]
    public void SchemaFingerprint_IsIndependentOfObjectPropertyOrder()
    {
        using var left = JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"integer"}}}""");
        using var right = JsonDocument.Parse("""{"properties":{"b":{"type":"integer"},"a":{"type":"string"}},"type":"object"}""");

        Assert.Equal(
            DurableJsonSchemaFingerprint.Create(left.RootElement),
            DurableJsonSchemaFingerprint.Create(right.RootElement));
    }

    [Theory]
    [InlineData("[1,2]", "[2,1]")]
    [InlineData("true", "false")]
    [InlineData("1", "2")]
    [InlineData("{\"type\":\"string\"}", "{\"type\":\"integer\"}")]
    public void SchemaFingerprint_DetectsStructuralChanges(string leftJson, string rightJson)
    {
        using var left = JsonDocument.Parse(leftJson);
        using var right = JsonDocument.Parse(rightJson);

        Assert.NotEqual(
            DurableJsonSchemaFingerprint.Create(left.RootElement),
            DurableJsonSchemaFingerprint.Create(right.RootElement));
    }

    [Fact]
    public void SchemaFingerprint_RejectsDuplicateProperties()
    {
        using var schema = JsonDocument.Parse("""{"type":"string","type":"number"}""");

        Assert.Throws<DurableConfigurationException>(
            () => DurableJsonSchemaFingerprint.Create(schema.RootElement));
    }

    [Fact]
    public async Task Activation_SeparatesOrdinaryModelResultFromStateReplacement()
    {
        var function = AIFunctionFactory.Create((string value) => $"accepted:{value}", "accept");
        var activation = new DurableToolActivation<TestState>
        {
            Function = function,
            CompleteState = (result, _) => ValueTask.FromResult(
                DurableStateUpdate<TestState>.Replace(new TestState(result!.ToString()!, 1))),
        };

        var result = await DurableToolActivationInvoker.InvokeAsync(
            activation,
            new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "item" }),
            DurableToolDispatchMode.Sequential);

        var modelResult = Assert.IsType<JsonElement>(result.ModelResult);
        Assert.Equal("accepted:item", modelResult.GetString());
        Assert.True(result.HasStateReplacement);
        Assert.Equal("accepted:item", result.StateReplacement!.Value.GetProperty("value").GetString());
        Assert.Equal(1, result.StateReplacement.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task StateUpdate_DistinguishesUnchangedReplaceValueAndReplaceNull()
    {
        var function = AIFunctionFactory.Create(() => "ok", "tool");

        var unchanged = await InvokeWithCompletion(
            function,
            DurableStateUpdate<TestState>.Unchanged);
        var replaced = await InvokeWithCompletion(
            function,
            DurableStateUpdate<TestState>.Replace(new TestState("value", 2)));
        var replacedNull = await InvokeWithCompletion(
            function,
            DurableStateUpdate<TestState>.Replace(null));

        Assert.False(unchanged.HasStateReplacement);
        Assert.Null(unchanged.StateReplacement);
        Assert.True(replaced.HasStateReplacement);
        Assert.Equal(JsonValueKind.Object, replaced.StateReplacement!.Value.ValueKind);
        Assert.True(replacedNull.HasStateReplacement);
        Assert.Equal(JsonValueKind.Null, replacedNull.StateReplacement!.Value.ValueKind);
    }

    [Fact]
    public async Task ParallelCompletion_IsRejectedBeforeFunctionInvocation()
    {
        var invocationCount = 0;
        var function = AIFunctionFactory.Create(() => ++invocationCount, "tool");
        var activation = new DurableToolActivation<int>
        {
            Function = function,
            CompleteState = (_, _) => ValueTask.FromResult(DurableStateUpdate<int>.Replace(1)),
        };

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => DurableToolActivationInvoker.InvokeAsync(
                activation,
                new AIFunctionArguments(),
                DurableToolDispatchMode.Parallel));

        Assert.True(exception.NonRetryable);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task CompletionFailure_IsNonRetryableAndFunctionRunsOnce()
    {
        var invocationCount = 0;
        var function = AIFunctionFactory.Create(() => ++invocationCount, "tool");
        var activation = new DurableToolActivation<int>
        {
            Function = function,
            CompleteState = (_, _) => throw new InvalidOperationException("completion failed"),
        };

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => DurableToolActivationInvoker.InvokeAsync(
                activation,
                new AIFunctionArguments(),
                DurableToolDispatchMode.Sequential));

        Assert.True(exception.NonRetryable);
        Assert.Equal(1, invocationCount);
    }

    private static Task<DurableToolActivationResult> InvokeWithCompletion(
        AIFunction function,
        DurableStateUpdate<TestState> update) =>
        DurableToolActivationInvoker.InvokeAsync(
            new DurableToolActivation<TestState>
            {
                Function = function,
                CompleteState = (_, _) => ValueTask.FromResult(update),
            },
            new AIFunctionArguments(),
            DurableToolDispatchMode.Sequential);

    private sealed record TestState(string Value, int Count);
}
