#if NETSTANDARD2_1 || COMPATIBILITY_GUARD_TESTS
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System;

// Actual runtime compatibility, separate from compiler metadata.
// Deliberately implements only the object/string/int overloads used here.
// C# 14 static extensions preserve existing guard call sites on netstandard2.1.
// No generic-math or unsafe-pointer API is claimed.
#if NETSTANDARD2_1
[Microsoft.CodeAnalysis.Embedded]
#endif
internal static class DownLevelArgumentGuards
{
    extension(ArgumentNullException)
    {
        public static void ThrowIfNull(
            [NotNull] object? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
    }

    extension(ArgumentException)
    {
        public static void ThrowIfNullOrEmpty(
            [NotNull] string? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (argument.Length == 0)
            {
                throw new ArgumentException("The value cannot be an empty string.", paramName);
            }
        }

        public static void ThrowIfNullOrWhiteSpace(
            [NotNull] string? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
            }
        }
    }

    extension(ArgumentOutOfRangeException)
    {
        public static void ThrowIfNegative(
            int value,
            [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, "The value must not be negative.");
            }
        }

        public static void ThrowIfNegativeOrZero(
            int value,
            [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be greater than zero.");
            }
        }

        public static void ThrowIfLessThan(
            int value,
            int other,
            [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value < other)
            {
                throw new ArgumentOutOfRangeException(paramName, value, $"The value must be greater than or equal to {other}.");
            }
        }
    }
}
#endif
