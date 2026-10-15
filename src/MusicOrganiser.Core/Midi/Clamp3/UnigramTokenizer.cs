using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// SentencePiece Unigram tokenizer (XLM-RoBERTa style) read from a Hugging Face tokenizer.json: the text model CLaMP 3
/// uses for descriptions. It follows the file's steps: the normaliser (XLM-R's "precompiled" one is SentencePiece's NFKC
/// mapping, done here as NFKC with control characters as spaces, then runs of spaces made one), Metaspace (spaces become
/// "▁", which starts each word), then each word cut into the pieces whose scores add up highest (Viterbi).
/// </summary>
public sealed partial class UnigramTokenizer
{
    const char Space = '▁';
    readonly Dictionary<string, (int Id, double Score)> _pieces;
    readonly int _maxPieceLength;
    readonly double _unkScore;
    readonly bool _prefixSpace;
    readonly List<Func<string, string>> _normalisers;

    public int UnkId { get; }
    public int BosId { get; }
    public int EosId { get; }
    public int PadId { get; }

    public UnigramTokenizer(IReadOnlyList<(string Piece, double Score)> vocab, int unkId, bool prefixSpace = true,
                            IEnumerable<Func<string, string>>? normalisers = null)
    {
        _pieces = new Dictionary<string, (int, double)>(vocab.Count, StringComparer.Ordinal);
        for (var i = 0; i < vocab.Count; i++)
        {
            _pieces.TryAdd(vocab[i].Piece, (i, vocab[i].Score));
            _maxPieceLength = Math.Max(_maxPieceLength, vocab[i].Piece.Length);
        }
        UnkId = unkId;
        _prefixSpace = prefixSpace;
        _normalisers = normalisers?.ToList() ?? [Precompiled, s => MultipleSpaces().Replace(s, " ")];
        // As the Hugging Face and SentencePiece Unigram models do: an unknown character costs more than any piece.
        _unkScore = vocab.Count == 0 ? -10 : vocab.Min(v => v.Score) - 10;
        BosId = Id("<s>", 0);
        PadId = Id("<pad>", 1);
        EosId = Id("</s>", 2);
    }

    int Id(string piece, int fallback) => _pieces.TryGetValue(piece, out var p) ? p.Id : fallback;

    public static UnigramTokenizer Load(string tokenizerJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJson));
        var root = doc.RootElement;
        var model = root.GetProperty("model");
        if (model.TryGetProperty("type", out var type) && type.GetString() != "Unigram")
            throw new InvalidDataException($"Expected a Unigram tokenizer, found {type.GetString()}.");
        var vocab = model.GetProperty("vocab").EnumerateArray().Select(v => (v[0].GetString()!, v[1].GetDouble())).ToList();
        var unk = model.TryGetProperty("unk_id", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 3;
        var prefix = true;
        if (root.TryGetProperty("pre_tokenizer", out var pre) && pre.ValueKind == JsonValueKind.Object)
        {
            var metaspace = pre.GetProperty("type").GetString() == "Metaspace" ? pre
                : pre.TryGetProperty("pretokenizers", out var list) ? list.EnumerateArray().FirstOrDefault(p => p.GetProperty("type").GetString() == "Metaspace")
                : default;
            if (metaspace.ValueKind == JsonValueKind.Object)
            {
                if (metaspace.TryGetProperty("add_prefix_space", out var aps) && aps.ValueKind is JsonValueKind.False) prefix = false;
                if (metaspace.TryGetProperty("prepend_scheme", out var ps) && ps.GetString() == "never") prefix = false;
            }
        }
        var normalisers = root.TryGetProperty("normalizer", out var norm) && norm.ValueKind == JsonValueKind.Object ? Normalisers(norm).ToList() : [];
        return new UnigramTokenizer(vocab, unk, prefix, normalisers);
    }

    static IEnumerable<Func<string, string>> Normalisers(JsonElement n)
    {
        switch (n.GetProperty("type").GetString())
        {
            case "Sequence":
                foreach (var inner in n.GetProperty("normalizers").EnumerateArray())
                    foreach (var f in Normalisers(inner)) yield return f;
                break;
            case "Precompiled": yield return Precompiled; break;
            case "NFKC": yield return s => s.Normalize(NormalizationForm.FormKC); break;
            case "NFC": yield return s => s.Normalize(NormalizationForm.FormC); break;
            case "NFKD": yield return s => s.Normalize(NormalizationForm.FormKD); break;
            case "NFD": yield return s => s.Normalize(NormalizationForm.FormD); break;
            case "Lowercase": yield return s => s.ToLowerInvariant(); break;
            case "Strip": yield return s => s.Trim(); break;
            case "Replace":
                var content = n.GetProperty("content").GetString() ?? "";
                var pattern = n.GetProperty("pattern");
                if (pattern.TryGetProperty("Regex", out var regex))
                {
                    var r = new Regex(regex.GetString()!, RegexOptions.CultureInvariant);
                    yield return s => r.Replace(s, content);
                }
                else if (pattern.TryGetProperty("String", out var literal) && literal.GetString() is { Length: > 0 } find)
                    yield return s => s.Replace(find, content, StringComparison.Ordinal);
                break;
        }
    }

    /// <summary>SentencePiece's nmt_nfkc, near enough: NFKC, and tabs, line breaks and other controls as spaces.</summary>
    static string Precompiled(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsWhiteSpace(c) || char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpaces();

    /// <summary>Ids for <paramref name="text"/>, with the start and end tokens, cut to <paramref name="maxLength"/>.</summary>
    public List<int> Encode(string text, int maxLength = 128)
    {
        var ids = new List<int> { BosId };
        foreach (var word in Words(_normalisers.Aggregate(text, (s, f) => f(s))))
            ids.AddRange(Viterbi(word));
        if (ids.Count > maxLength - 1) ids = ids[..(maxLength - 1)];
        ids.Add(EosId);
        return ids;
    }

    /// <summary>The pieces themselves, for tests and for seeing how a description was read.</summary>
    public List<string> Pieces(string text)
    {
        var byId = _pieces.ToDictionary(p => p.Value.Id, p => p.Key);
        return Encode(text, int.MaxValue)[1..^1].Select(i => byId.GetValueOrDefault(i, "<unk>")).ToList();
    }

    /// <summary>
    /// Metaspace: spaces become "▁" (with one in front unless there is one already), and each "▁" starts a new word.
    /// </summary>
    IEnumerable<string> Words(string text)
    {
        if (text.Length == 0) yield break;
        var s = text.Replace(' ', Space);
        if (_prefixSpace && s[0] != Space) s = Space + s;
        var start = 0;
        for (var i = 1; i < s.Length; i++)
            if (s[i] == Space)
            {
                yield return s[start..i];
                start = i;
            }
        yield return s[start..];
    }
    /// <summary>The best-scoring way to cut a word into known pieces; characters no piece covers become the unknown token.</summary>
    List<int> Viterbi(string word)
    {
        // Positions are in UTF-16 units, but a piece never starts or ends inside a surrogate pair.
        var n = word.Length;
        var best = new double[n + 1];
        var from = new int[n + 1];
        var piece = new int[n + 1];
        Array.Fill(best, double.NegativeInfinity);
        best[0] = 0;
        for (var start = 0; start < n; start++)
        {
            if (double.IsNegativeInfinity(best[start]) || char.IsLowSurrogate(word[start])) continue;
            var found = false;
            for (var len = 1; len <= _maxPieceLength && start + len <= n; len++)
            {
                var end = start + len;
                if (end < n && char.IsLowSurrogate(word[end])) continue;
                if (!_pieces.TryGetValue(word.Substring(start, len), out var p)) continue;
                var score = best[start] + p.Score;
                if (score > best[end]) { best[end] = score; from[end] = start; piece[end] = p.Id; }
                if (len == 1 || (len == 2 && char.IsHighSurrogate(word[start]))) found = true;
            }
            if (!found)
            {
                // No piece for this one character: it's unknown.
                var end = start + (char.IsHighSurrogate(word[start]) && start + 1 < n ? 2 : 1);
                var score = best[start] + _unkScore;
                if (score > best[end]) { best[end] = score; from[end] = start; piece[end] = UnkId; }
            }
        }
        var ids = new List<int>();
        for (var at = n; at > 0; at = from[at]) ids.Add(piece[at]);
        ids.Reverse();
        // Runs of unknown characters are one unknown token, as in the Hugging Face tokenizer.
        var fused = new List<int>(ids.Count);
        foreach (var id in ids)
            if (!(id == UnkId && fused.Count > 0 && fused[^1] == UnkId)) fused.Add(id);
        return fused;
    }
}
