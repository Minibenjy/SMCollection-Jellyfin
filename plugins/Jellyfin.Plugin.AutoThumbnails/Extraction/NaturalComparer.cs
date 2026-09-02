using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AutoThumbnails.Extraction;

/// <summary>
/// Orders names the way a human reads them, so that "page2" sorts before "page10".
/// Plain ordinal sorting would pick the wrong first page in most scanned comics.
/// </summary>
public sealed class NaturalComparer : IComparer<string?>
{
    /// <summary>
    /// Gets the shared instance.
    /// </summary>
    public static NaturalComparer Instance { get; } = new NaturalComparer();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (x is null)
        {
            return y is null ? 0 : -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startI = i;
                var startJ = j;
                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                var numX = x.AsSpan(startI, i - startI).TrimStart('0');
                var numY = y.AsSpan(startJ, j - startJ).TrimStart('0');

                if (numX.Length != numY.Length)
                {
                    return numX.Length - numY.Length;
                }

                var cmp = numX.CompareTo(numY, StringComparison.Ordinal);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
            else
            {
                var cmp = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                if (cmp != 0)
                {
                    return cmp;
                }

                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
