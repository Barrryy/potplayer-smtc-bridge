namespace SmtcBridge;

/// <summary>
/// 「伪正则」文件名解析 —— 与 C++ 侧 MatchPattern 保持同一套语义。
///
/// 语法：%字段% 为占位符，其余字符按字面量匹配，%% 表示字面量 %。
/// 支持字段：%title% %artist% %album% %albumArtist% %track%
///
/// 匹配顺序：逐个字面量在剩余文本中找第一次出现的位置，
/// 它之前的文本归给上一个占位符；最后挂起的占位符吃掉剩余全部文本。
/// 界面用它做实时预览，DLL 用它做真实取值，两边规则完全一致。
/// </summary>
internal static class MetaRules
{
    internal sealed record Preview(string Title, string Artist, string Album,
        string AlbumArtist, int Track, bool Matched, string Note);

    private sealed record Element(bool IsField, string Text);

    public static string StripExtension(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    public static Preview Apply(string fileName, string pattern)
    {
        var stem = StripExtension(fileName.Trim());
        if (string.IsNullOrEmpty(stem)) return new Preview("", "", "", "", 0, false, "文件名为空");
        if (string.IsNullOrWhiteSpace(pattern)) pattern = BridgeConfig.DefaultPattern;

        var elements = Tokenize(pattern);
        if (elements.Count == 0)
            return new Preview(stem, "", "", "", 0, true, "规则中没有占位符，整体作为标题");

        string title = "", artist = "", album = "", albumArtist = "";
        var track = 0;
        string? pending = null;

        void Assign(string field, string value)
        {
            value = value.Trim();
            switch (field.ToLowerInvariant())
            {
                case "title": title = value; break;
                case "artist": artist = value; break;
                case "album": album = value; break;
                case "albumartist": albumArtist = value; break;
                case "track": int.TryParse(value, out track); break;
            }
        }

        var lower = stem.ToLowerInvariant();
        var pos = 0;
        var failed = false;

        foreach (var element in elements)
        {
            if (element.IsField)
            {
                if (pending is not null) Assign(pending, "");
                pending = element.Text;
                continue;
            }
            if (element.Text.Length == 0) continue;

            var idx = lower.IndexOf(element.Text.ToLowerInvariant(), pos, StringComparison.Ordinal);
            if (idx < 0) { failed = true; break; }
            if (pending is not null)
            {
                Assign(pending, stem[pos..idx]);
                pending = null;
            }
            pos = idx + element.Text.Length;
        }
        if (!failed && pending is not null) Assign(pending, stem[pos..]);

        var captured = title.Length + artist.Length + album.Length + albumArtist.Length > 0 ||
                       track > 0;
        if (failed || !captured)
        {
            return new Preview(stem, "", "", "", 0, false,
                failed ? "规则没匹配上，回退为「整体作为标题」" : "规则没捕获到任何字段，回退为「整体作为标题」");
        }

        var note = $"匹配成功：{Describe(title, artist, album, albumArtist, track)}";
        return new Preview(title, artist, album, albumArtist, track, true, note);
    }

    private static string Describe(string title, string artist, string album, string albumArtist,
        int track)
    {
        var parts = new List<string>();
        if (title.Length > 0) parts.Add($"标题={title}");
        if (artist.Length > 0) parts.Add($"歌手={artist}");
        if (album.Length > 0) parts.Add($"专辑={album}");
        if (albumArtist.Length > 0) parts.Add($"专辑歌手={albumArtist}");
        if (track > 0) parts.Add($"音轨={track}");
        return string.Join("，", parts);
    }

    private static List<Element> Tokenize(string pattern)
    {
        var elements = new List<Element>();
        var literal = new System.Text.StringBuilder();

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '%')
            {
                literal.Append(pattern[i]);
                continue;
            }
            var end = pattern.IndexOf('%', i + 1);
            if (end < 0)
            {
                literal.Append(pattern[i..]);
                break;
            }
            var field = pattern[(i + 1)..end];
            if (field.Length == 0)
            {
                literal.Append('%');   // %% → 字面量 %
            }
            else
            {
                if (literal.Length > 0)
                {
                    elements.Add(new Element(false, literal.ToString()));
                    literal.Clear();
                }
                elements.Add(new Element(true, field));
            }
            i = end;
        }

        if (literal.Length > 0) elements.Add(new Element(false, literal.ToString()));
        return elements;
    }
}
