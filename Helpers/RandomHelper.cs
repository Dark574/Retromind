using System;
using System.Collections.Generic;
using System.Linq;

namespace Retromind.Helpers;

public static class RandomHelper
{
    // Einzige Instanz für die gesamte App (vermeidet Seed-Probleme)
    private static readonly Random _rng = new Random();

    /// <summary>
    /// Wählt ein zufälliges Element aus einer Liste oder Collection aus.
    /// </summary>
    /// <typeparam name="T">Der Typ der Elemente (z.B. MediaItem oder GalleryImage)</typeparam>
    public static T? PickRandom<T>(IEnumerable<T>? source)
    {
        if (source == null) return default;

        // Optimierung für Listen/Arrays (direkter Index-Zugriff)
        if (source is IList<T> list)
        {
            if (list.Count == 0) return default;
            return list[_rng.Next(list.Count)];
        }

        // Fallback für andere Collections (z.B. LINQ Results)
        var array = source.ToArray();
        if (array.Length == 0) return default;
        
        return array[_rng.Next(array.Length)];
    }

    /// <summary>
    /// Wählt mehrere einzigartige zufällige Elemente aus (z.B. für "3 zufällige Spiele vorschlagen")
    /// </summary>
    public static List<T> PickRandomMultiple<T>(IEnumerable<T>? source, int count)
    {
        if (source == null) return new List<T>();
        
        var list = source.ToList();
        if (list.Count <= count) return list; // Wenn weniger da sind als gefordert, alle zurückgeben

        var results = new List<T>();
        var availableIndices = Enumerable.Range(0, list.Count).ToList();

        for (int i = 0; i < count; i++)
        {
            var indexToRemove = _rng.Next(availableIndices.Count);
            var selectedIndex = availableIndices[indexToRemove];
            
            results.Add(list[selectedIndex]);
            availableIndices.RemoveAt(indexToRemove); // Index entfernen, damit er nicht doppelt kommt
        }

        return results;
    }

    /// <summary>
    /// Gibt true oder false zurück (Münzwurf).
    /// </summary>
    public static bool CoinFlip()
    {
        return _rng.Next(2) == 0;
    }
    
    /// <summary>
    /// Gibt eine zufällige Zahl zwischen min (inklusive) und max (exklusive) zurück.
    /// </summary>
    public static int Next(int min, int max)
    {
        return _rng.Next(min, max);
    }
}