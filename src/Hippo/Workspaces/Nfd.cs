using System.Runtime.InteropServices;
using System.Text;

namespace Hippo.Workspaces;

/// <summary>
/// Unicode canonical decomposition (NFD): the form two strings share exactly when they are equal under NFC, so hippo
/// compares paths in it. A link written as <c>café</c> with a combining accent then reaches a file whose name has the
/// precomposed <c>é</c>, as macOS opens it. hippo runs with invariant globalization, where
/// <see cref="string.Normalize()"/> returns its input, so the tables come from <c>scripts/nfd-tables.py</c>.
/// </summary>
internal static partial class Nfd
{
    private const int HangulFirst = 0xAC00, HangulCount = 11172;
    private const int LeadFirst = 0x1100, VowelFirst = 0x1161, TrailFirst = 0x11A7;
    private const int VowelCount = 21, TrailCount = 28;

    /// <summary>The canonical decomposition of <paramref name="text"/>. A lone surrogate is kept as it is.</summary>
    public static string Of(string text)
    {
        // Nothing below U+00C0 decomposes or combines, so most paths are returned as they are.
        var first = text.AsSpan().IndexOfAnyExceptInRange('\0', '¿');
        if (first < 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 8).Append(text, 0, first);
        // The code points from the last starter on, with their combining classes, held until the next starter so their
        // marks can be put in order.
        var pending = new List<Mark>();
        for (var i = first; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            var cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            if (cp - HangulFirst is >= 0 and < HangulCount and var s)
            {
                Add(LeadFirst + s / (VowelCount * TrailCount));
                Add(VowelFirst + s % (VowelCount * TrailCount) / TrailCount);
                if (s % TrailCount != 0)
                {
                    Add(TrailFirst + s % TrailCount);
                }
            }
            else if (Decomposed.BinarySearch(cp) is >= 0 and var index)
            {
                foreach (var part in Decompositions[DecompositionStarts[index]..DecompositionStarts[index + 1]])
                {
                    Add(part);
                }
            }
            else
            {
                Add(cp);
            }
        }
        Flush(pending, result);
        return result.ToString();

        void Add(int cp)
        {
            var cc = CombiningClass(cp);
            if (cc == 0)
            {
                Flush(pending, result);
            }
            pending.Add(new Mark(cp, cc, pending.Count));
        }
    }

    /// <summary>A code point held by <see cref="Of"/>, with its combining class and its place in the run.</summary>
    private readonly record struct Mark(int CodePoint, int Class, int Order);

    /// <summary>Appends <paramref name="pending"/>, the marks after its starter in canonical order, and clears it. The
    /// sort is stable, so marks of one class keep the order they were written in.</summary>
    private static void Flush(List<Mark> pending, StringBuilder result)
    {
        var marks = CollectionsMarshal.AsSpan(pending);
        if (marks.Length > 0 && marks[0].Class == 0)
        {
            marks = marks[1..];
        }
        // Ordered by class, then by place, which makes the sort stable. A run of marks can be as long as a page makes
        // it, so the sort must stay O(n log n) rather than an insertion sort's O(n²).
        marks.Sort(static (a, b) => a.Class != b.Class ? a.Class.CompareTo(b.Class) : a.Order.CompareTo(b.Order));
        foreach (var (cp, _, _) in pending)
        {
            // A lone surrogate is a char of its own, which ConvertFromUtf32 refuses.
            if (cp <= char.MaxValue)
            {
                result.Append((char)cp);
            }
            else
            {
                result.Append(char.ConvertFromUtf32(cp));
            }
        }
        pending.Clear();
    }

    private static int CombiningClass(int cp)
    {
        var index = CombiningFirst.BinarySearch(cp);
        if (index < 0)
        {
            index = ~index - 1;
        }
        return index >= 0 && cp <= CombiningLast[index] ? CombiningClasses[index] : 0;
    }
}
