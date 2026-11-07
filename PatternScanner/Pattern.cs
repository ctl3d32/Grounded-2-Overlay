using System.Globalization;

namespace G2Overlay.PatternScanner;

/// <summary>
/// Padrão AOB no formato IDA: "48 8B 05 ?? ?? ?? ?? 48 85 C0" ("?" e "??" são curingas).
/// </summary>
public sealed class Pattern
{
    public string Text { get; }
    public byte[] Bytes { get; }
    public bool[] Solid { get; }
    public int Length => Bytes.Length;

    // Primeiro byte não-curinga: usado com Span.IndexOf (vetorizado) para acelerar a busca.
    private readonly int _anchor;

    private Pattern(string text, byte[] bytes, bool[] solid, int anchor)
    {
        Text = text;
        Bytes = bytes;
        Solid = solid;
        _anchor = anchor;
    }

    public static Pattern Parse(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw new FormatException("Padrão vazio.");

        var bytes = new byte[tokens.Length];
        var solid = new bool[tokens.Length];
        int anchor = -1;
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];
            if (t == "?" || t == "??")
            {
                solid[i] = false;
                continue;
            }

            if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new FormatException($"Token inválido '{t}' no padrão '{text}'.");
            solid[i] = true;
            if (anchor < 0) anchor = i;
        }

        if (anchor < 0) throw new FormatException($"Padrão sem bytes fixos: '{text}'.");
        return new Pattern(text, bytes, solid, anchor);
    }

    private bool MatchAt(ReadOnlySpan<byte> data, int pos)
    {
        for (int i = 0; i < Bytes.Length; i++)
        {
            if (Solid[i] && data[pos + i] != Bytes[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// Procura ocorrências em data[start..]. Adiciona os índices em results (até maxResults).
    /// </summary>
    public void FindAll(ReadOnlySpan<byte> data, int start, int limitStart, List<int> results, int maxResults)
    {
        // limitStart: maior posição inicial permitida (exclusivo) — evita contar duas vezes a sobreposição entre blocos.
        int lastStart = Math.Min(data.Length - Length, limitStart - 1);
        int pos = start;
        byte a = Bytes[_anchor];
        while (pos <= lastStart && results.Count < maxResults)
        {
            int searchFrom = pos + _anchor;
            int searchLen = lastStart - pos + 1;
            int rel = data.Slice(searchFrom, searchLen).IndexOf(a);
            if (rel < 0) return;
            int cand = pos + rel;
            if (MatchAt(data, cand)) results.Add(cand);
            pos = cand + 1;
        }
    }

    public override string ToString() => Text;
}
