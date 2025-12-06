using System;
using System.Collections.Generic;
using System.Linq;

namespace Retromind.Helpers;

/// <summary>
/// Provides static helper methods for randomization using a thread-safe RNG.
/// </summary>
public static class RandomHelper
{
    // Using Random.Shared (available since .NET 6) ensures thread safety automatically.
    // No need for custom locks or [ThreadStatic].

    /// <summary>
    /// Picks a single random element from a collection.
    /// </summary>
    /// <typeparam name="T">The type of elements.</typeparam>
    /// <param name="source">The source collection.</param>
    /// <returns>A random element or default if empty/null.</returns>
    public static T? PickRandom<T>(IEnumerable<T>? source)
    {
        if (source == null) return default;

        // Optimization for Lists/Arrays (Direct Index Access O(1))
        if (source is IList<T> list)
        {
            if (list.Count == 0) return default;
            return list[Random.Shared.Next(list.Count)];
        }

        // Fallback for generic IEnumerables (requires buffering O(N))
        var array = source.ToArray();
        if (array.Length == 0) return default;
        
        return array[Random.Shared.Next(array.Length)];
    }

    /// <summary>
    /// Picks multiple unique random elements from a collection.
    /// Uses a partial Fisher-Yates shuffle for O(N) performance.
    /// </summary>
    public static List<T> PickRandomMultiple<T>(IEnumerable<T>? source, int count)
    {
        if (source == null) return new List<T>();
        
        // Copy to list to avoid modifying original or multiple enumerations
        var list = source.ToList();
        
        if (list.Count <= count) return list; 

        // Fisher-Yates Shuffle (Partial)
        // We only shuffle as many items as we need to return.
        for (int i = 0; i < count; i++)
        {
            int j = Random.Shared.Next(i, list.Count);
            (list[i], list[j]) = (list[j], list[i]); // Swap
        }

        // Return the first 'count' elements which are now randomized unique items
        return list.GetRange(0, count);
    }

    /// <summary>
    /// Returns true or false with 50% probability (Coin Flip).
    /// </summary>
    public static bool CoinFlip()
    {
        return Random.Shared.Next(2) == 0;
    }
    
    /// <summary>
    /// Returns a random integer between min (inclusive) and max (exclusive).
    /// </summary>
    public static int Next(int min, int max)
    {
        return Random.Shared.Next(min, max);
    }
}