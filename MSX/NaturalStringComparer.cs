namespace MSX;

public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                int startX = ix;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                int lenX = ix - startX;

                int startY = iy;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;
                int lenY = iy - startY;

                var numSpanX = x.AsSpan(startX, lenX).TrimStart('0');
                var numSpanY = y.AsSpan(startY, lenY).TrimStart('0');

                if (numSpanX.Length != numSpanY.Length)
                {
                    return numSpanX.Length.CompareTo(numSpanY.Length);
                }

                int cmp = numSpanX.CompareTo(numSpanY, StringComparison.Ordinal);
                if (cmp != 0) return cmp;

                if (lenX != lenY)
                {
                    return lenX.CompareTo(lenY);
                }
            }
            else
            {
                int cmp = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                if (cmp != 0) return cmp;
                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}

