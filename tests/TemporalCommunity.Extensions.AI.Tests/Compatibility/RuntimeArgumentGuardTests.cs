using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Compatibility;

public class RuntimeArgumentGuardTests
{
    [Fact]
    public void NullGuard_PreservesCallerExpressionAndExplicitParameterName()
    {
        object? missing = null;
        Assert.Equal("missing", Assert.Throws<ArgumentNullException>(
            () => DownLevelArgumentGuards.ThrowIfNull(missing)).ParamName);
        Assert.Equal("custom", Assert.Throws<ArgumentNullException>(
            () => DownLevelArgumentGuards.ThrowIfNull(missing, "custom")).ParamName);
        Assert.Null(Assert.Throws<ArgumentNullException>(
            () => DownLevelArgumentGuards.ThrowIfNull(missing, null)).ParamName);
        DownLevelArgumentGuards.ThrowIfNull(new object());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u00a0")]
    public void StringGuards_MatchFrameworkExceptionTypesAndParameterNames(string? argument)
    {
        Compare(() => ArgumentException.ThrowIfNullOrEmpty(argument),
            () => DownLevelArgumentGuards.ThrowIfNullOrEmpty(argument));
        Compare(() => ArgumentException.ThrowIfNullOrWhiteSpace(argument),
            () => DownLevelArgumentGuards.ThrowIfNullOrWhiteSpace(argument));
        Compare(() => ArgumentException.ThrowIfNullOrEmpty(argument, "custom"),
            () => DownLevelArgumentGuards.ThrowIfNullOrEmpty(argument, "custom"));
        Compare(() => ArgumentException.ThrowIfNullOrWhiteSpace(argument, null),
            () => DownLevelArgumentGuards.ThrowIfNullOrWhiteSpace(argument, null));
    }

    [Theory]
    [InlineData("x")]
    [InlineData(" x ")]
    public void StringGuards_AcceptNonWhitespace(string argument)
    {
        DownLevelArgumentGuards.ThrowIfNullOrEmpty(argument);
        DownLevelArgumentGuards.ThrowIfNullOrWhiteSpace(argument);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void IntegerGuards_MatchFrameworkBoundariesAndActualValue(int value)
    {
        Compare(() => ArgumentOutOfRangeException.ThrowIfNegative(value),
            () => DownLevelArgumentGuards.ThrowIfNegative(value));
        Compare(() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value),
            () => DownLevelArgumentGuards.ThrowIfNegativeOrZero(value));
        Compare(() => ArgumentOutOfRangeException.ThrowIfLessThan(value, 4),
            () => DownLevelArgumentGuards.ThrowIfLessThan(value, 4));
        Compare(() => ArgumentOutOfRangeException.ThrowIfLessThan(value, 4, "custom"),
            () => DownLevelArgumentGuards.ThrowIfLessThan(value, 4, "custom"));
        Compare(() => ArgumentOutOfRangeException.ThrowIfNegative(value, null),
            () => DownLevelArgumentGuards.ThrowIfNegative(value, null));
    }

    [Fact]
    public void Guards_PreserveNullableFlow()
    {
        object? item = new object();
        string? text = "valid";
        DownLevelArgumentGuards.ThrowIfNull(item);
        DownLevelArgumentGuards.ThrowIfNullOrWhiteSpace(text);
        Assert.NotEmpty(item.ToString()!);
        Assert.Equal(5, text.Length);
    }

    private static void Compare(Action native, Action handwritten)
    {
        var expected = Record.Exception(native);
        var actual = Record.Exception(handwritten);
        Assert.Equal(expected?.GetType(), actual?.GetType());
        if (expected is ArgumentException expectedArgument)
        {
            Assert.Equal(expectedArgument.ParamName, Assert.IsAssignableFrom<ArgumentException>(actual).ParamName);
        }
        if (expected is ArgumentOutOfRangeException expectedRange)
        {
            Assert.Equal(expectedRange.ActualValue, Assert.IsType<ArgumentOutOfRangeException>(actual).ActualValue);
        }
    }
}
