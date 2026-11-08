using System.Collections.Generic;

namespace Retromind.Helpers;

internal static class SelectionIndexHelper
{
    // A binding or a collection replacement may leave an index stale. Never trust
    // it without checking the item; ordinary controller steps then need no scan.
    internal static int Resolve<T>(IList<T> source, T? selected, int candidateIndex = -1)
    {
        if (selected is null)
            return -1;

        if (candidateIndex >= 0 && candidateIndex < source.Count &&
            EqualityComparer<T>.Default.Equals(source[candidateIndex], selected))
            return candidateIndex;

        return source.IndexOf(selected);
    }
}
