using System.Text;

namespace RtxAutoCurveOptimizer.Afterburner;

/// <summary>
/// Line-preserving INI document: every line keeps its own terminator and the file's
/// encoding is restored on save, so untouched content is written back byte for byte.
/// </summary>
internal sealed class IniDocument
{
    private sealed class Line(string text, string eol)
    {
        public string Text { get; set; } = text;
        public string Eol { get; set; } = eol;
    }

    internal readonly record struct Section(string Name, int Start, int End);

    private readonly List<Line> _lines = [];
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private readonly string _newline;

    private IniDocument(Encoding encoding, byte[] preamble, string newline)
    {
        _encoding = encoding;
        _preamble = preamble;
        _newline = newline;
    }

    /// <summary>Decodes the bytes (BOM-aware, otherwise a lossless 1:1 byte mapping) into lines.</summary>
    public static IniDocument Parse(byte[] bytes)
    {
        Encoding encoding;
        int preamble;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(false);
            preamble = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(false, false);
            preamble = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(true, false);
            preamble = 2;
        }
        else
        {
            encoding = Encoding.Latin1;
            preamble = 0;
        }

        string text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        var lines = new List<Line>();
        string? firstEol = null;
        int pos = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl < 0)
            {
                lines.Add(new Line(text[pos..], ""));
                break;
            }

            bool cr = nl > pos && text[nl - 1] == '\r';
            string eol = cr ? "\r\n" : "\n";
            lines.Add(new Line(text[pos..(cr ? nl - 1 : nl)], eol));
            firstEol ??= eol;
            pos = nl + 1;
        }

        var doc = new IniDocument(encoding, bytes[..preamble], firstEol ?? "\r\n");
        doc._lines.AddRange(lines);
        return doc;
    }

    /// <summary>Encodes the document back to bytes.</summary>
    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
            sb.Append(line.Text).Append(line.Eol);
        byte[] body = _encoding.GetBytes(sb.ToString());
        var result = new byte[_preamble.Length + body.Length];
        _preamble.CopyTo(result, 0);
        body.CopyTo(result, _preamble.Length);
        return result;
    }

    /// <summary>All sections in file order with their line ranges (header line to next header, exclusive).</summary>
    public IReadOnlyList<Section> Sections()
    {
        var result = new List<Section>();
        string? name = null;
        int start = 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            string? header = SectionName(_lines[i].Text);
            if (header is null)
                continue;
            if (name is not null)
                result.Add(new Section(name, start, i));
            name = header;
            start = i;
        }
        if (name is not null)
            result.Add(new Section(name, start, _lines.Count));
        return result;
    }

    /// <summary>Finds a section by name (case-insensitive).</summary>
    public Section? FindSection(string name)
    {
        foreach (var s in Sections())
        {
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }

    /// <summary>Returns the trimmed value of a key, or null if section or key is missing.</summary>
    public string? GetValue(string section, string key)
    {
        if (FindSection(section) is not { } s)
            return null;
        int index = FindKeyLine(s, key);
        return index < 0 ? null : SplitKey(_lines[index].Text)!.Value.Value.Trim();
    }

    /// <summary>Sets a key in an existing section: replaces its value in place or appends the key after the last entry.</summary>
    public void SetValue(string section, string key, string value)
    {
        var s = FindSection(section) ?? throw new InvalidOperationException($"Section [{section}] does not exist.");
        int index = FindKeyLine(s, key);
        if (index >= 0)
        {
            _lines[index].Text = SplitKey(_lines[index].Text)!.Value.Key + "=" + value;
            return;
        }

        int last = s.End - 1;
        while (last > s.Start && string.IsNullOrWhiteSpace(_lines[last].Text))
            last--;
        InsertLines(last + 1, [key + "=" + value]);
    }

    /// <summary>Inserts a new section (header plus entries) before the line at <paramref name="lineIndex"/>.</summary>
    public void InsertSection(int lineIndex, string name, IEnumerable<KeyValuePair<string, string>> entries)
    {
        var texts = new List<string> { "[" + name + "]" };
        texts.AddRange(entries.Select(e => e.Key + "=" + e.Value));
        InsertLines(lineIndex, texts);
    }

    /// <summary>Number of lines in the document.</summary>
    public int LineCount => _lines.Count;

    private void InsertLines(int index, IReadOnlyList<string> texts)
    {
        if (index > 0 && _lines[index - 1].Eol.Length == 0)
            _lines[index - 1].Eol = _newline;
        _lines.InsertRange(index, texts.Select(t => new Line(t, _newline)));
    }

    private int FindKeyLine(Section s, string key)
    {
        for (int i = s.Start + 1; i < s.End; i++)
        {
            var kv = SplitKey(_lines[i].Text);
            if (kv is not null && string.Equals(kv.Value.Key, key, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static string? SectionName(string text)
    {
        string t = text.Trim();
        return t.Length >= 2 && t[0] == '[' && t[^1] == ']' ? t[1..^1] : null;
    }

    private static (string Key, string Value)? SplitKey(string text)
    {
        string t = text.TrimStart();
        if (t.Length == 0 || t[0] is ';' or '#' or '[')
            return null;
        int eq = t.IndexOf('=');
        return eq <= 0 ? null : (t[..eq].Trim(), t[(eq + 1)..]);
    }
}
