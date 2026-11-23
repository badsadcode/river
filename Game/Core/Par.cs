using System;
using System.Threading.Tasks;

namespace River.Core;

/// <summary>
/// Every parallel loop in generation goes through here. By default it uses all cores (headless tests, start-up);
/// while the game streams, the worker limits it (<see cref="LeaveFree"/>) so the main thread always has a core and
/// frames don't stutter. Loop bodies are independent, so results don't depend on the degree of parallelism.
/// </summary>
public static class Par
{
    static ParallelOptions _options = new();

    public static void For(int fromInclusive, int toExclusive, Action<int> body) =>
        Parallel.For(fromInclusive, toExclusive, _options, body);

    /// <summary>Use all cores but <paramref name="cores"/> (at least one).</summary>
    public static void LeaveFree(int cores) =>
        _options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - cores) };

    public static void UseAll() => _options = new ParallelOptions();
}
