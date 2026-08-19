using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class DurableFunctionDeclarationSnapshotTests
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

    [Theory]
    [InlineData("1", "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b")]
    [InlineData("1.0", "d0ff5974b6aa52cf562bea5921840c032a860a91a3512f7fe8f768f6bbe005f6")]
    [InlineData("1.00", "cf9dcf6da8a82be1335c398a4005def7ee3a53d4698c59dbc6b2b14e72d1263c")]
    public void SchemaFingerprint_VersionOnePreservesNumericRepresentation(
        string json,
        string expectedFingerprint)
    {
        using var schema = JsonDocument.Parse(json);

        var fingerprint = DurableJsonSchemaFingerprint.Create(schema.RootElement);

        Assert.Equal(expectedFingerprint, fingerprint);
    }

    [Fact]
    public void SchemaFingerprint_NumberOutsideDoubleRange_UsesValidatedRawNumber()
    {
        using var schema = JsonDocument.Parse("1e400");

        var fingerprint = DurableJsonSchemaFingerprint.Create(schema.RootElement);

        Assert.Equal(
            "f2bba4568fecd4b9729970732e571ac9373a33fb2d6a960794a41f0f2ecdbc25",
            fingerprint);
    }

    [Fact]
    public void ValidateImplementation_DuplicateSchemaProperty_FailsNonRetryably()
    {
        var declared = AIFunctionFactory.Create(() => "declared", "tool");
        var snapshot = DurableFunctionDeclarationSnapshot.Create(declared.AsDeclarationOnly());
        var implementation = new DuplicatePropertyFunction();

        var exception = Assert.Throws<ApplicationFailureException>(
            () => snapshot.ValidateImplementation(implementation));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
        Assert.IsType<DurableConfigurationException>(exception.InnerException);
    }

    private sealed class DuplicatePropertyFunction : AIFunction
    {
        private static readonly JsonDocument Schema =
            JsonDocument.Parse("""{"type":"string","type":"number"}""");

        public override string Name => "tool";

        public override string Description => string.Empty;

        public override JsonElement JsonSchema => Schema.RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => ValueTask.FromResult<object?>("implemented");
    }
}
