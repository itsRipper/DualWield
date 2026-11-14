using System.Collections.Generic;

namespace DualWieldLab;

// A pair whose native path failed after an inventory mutation stays unpaired for the raid; a retry
// could fire or consume twice. Other pistols keep pairing. The ids are unordered (either hand).
internal sealed class PairQuarantine
{
    private readonly HashSet<string> _pairs = new HashSet<string>();

    internal void Add(string first, string second) => _pairs.Add(Key(first, second));

    internal bool Contains(string first, string second) => _pairs.Count > 0 && _pairs.Contains(Key(first, second));

    internal void Clear() => _pairs.Clear();

    private static string Key(string first, string second)
        => string.CompareOrdinal(first, second) <= 0 ? first + "|" + second : second + "|" + first;
}
