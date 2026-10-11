using System.Runtime.CompilerServices;

namespace Bsync.Testing;

/// <summary>Minimal assertions with no test-framework dependency, so cases can run in any host.</summary>
public static class Check
{
    /// <summary>Fails unless <paramref name="actual"/> equals <paramref name="expected"/>.</summary>
    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new ConformanceFailure($"{expression}: expected '{expected}', got '{actual}'.");
        }
    }

    /// <summary>Fails unless the sequences are equal element by element.</summary>
    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
        {
            throw new ConformanceFailure($"{expression}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}].");
        }
    }

    /// <summary>Fails unless <paramref name="condition"/> is true.</summary>
    public static void True(bool condition, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw new ConformanceFailure($"Expected true: {expression}.");
        }
    }

    /// <summary>Fails unless <paramref name="value"/> is null.</summary>
    public static void Null(object? value, [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (value is not null)
        {
            throw new ConformanceFailure($"Expected null: {expression}.");
        }
    }

    /// <summary>Fails unless <paramref name="action"/> throws <typeparamref name="TException"/> or a subclass.</summary>
    public static async Task Throws<TException>(Func<Task> action, [CallerArgumentExpression(nameof(action))] string? expression = null)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        catch (Exception other)
        {
            throw new ConformanceFailure($"{expression}: expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}");
        }

        throw new ConformanceFailure($"{expression}: expected {typeof(TException).Name}, nothing was thrown.");
    }
}
