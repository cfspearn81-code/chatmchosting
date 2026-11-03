using System.Globalization;
using System.Text;

namespace Harbor.Management;

/// <summary>Java-properties logical lines, preserving comments and untouched entries.</summary>
public sealed class JavaProperties
{
    private sealed record Entry(string Raw, string? Key, string? Value);
    private readonly List<Entry> entries = [];
    public IReadOnlyDictionary<string, string> Values => entries.Where(e => e.Key is not null)
        .GroupBy(e => e.Key!).ToDictionary(g => g.Key, g => g.Last().Value!);
    public static JavaProperties Parse(string text)
    {
        var result = new JavaProperties();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var raw = line;
            while (HasContinuation(line))
            {
                var next = reader.ReadLine();
                if (next is null) break;
                raw += "\n" + next;
                line = line[..^1] + next.TrimStart(' ', '\t', '\f');
            }
            var trimmed = line.TrimStart(' ', '\t', '\f');
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!') { result.entries.Add(new(raw, null, null)); continue; }
            var split = 0;
            var escaped = false;
            for (; split < trimmed.Length; split++)
            {
                var c = trimmed[split];
                if (!escaped && (c is '=' or ':' or ' ' or '\t' or '\f')) break;
                escaped = c == '\\' && !escaped;
            }
            var valueStart = split;
            while (valueStart < trimmed.Length && trimmed[valueStart] is ' ' or '\t' or '\f') valueStart++;
            if (valueStart < trimmed.Length && trimmed[valueStart] is '=' or ':') valueStart++;
            while (valueStart < trimmed.Length && trimmed[valueStart] is ' ' or '\t' or '\f') valueStart++;
            result.entries.Add(new(raw, Unescape(trimmed[..split]), Unescape(trimmed[valueStart..])));
        }
        return result;
    }
    public void Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Property name cannot be empty.", nameof(key));
        Validate(key, value);
        var replacement = new Entry(Escape(key, true) + "=" + Escape(value, false), key, value);
        var index = entries.FindLastIndex(e => e.Key == key);
        if (index < 0) entries.Add(replacement);
        else
        {
            entries[index] = replacement;
            for (var i = index - 1; i >= 0; i--) if (entries[i].Key == key) entries.RemoveAt(i);
        }
    }
    public override string ToString() => string.Join('\n', entries.Select(e => e.Raw)) + "\n";
    public static void Validate(string key, string value)
    {
        switch (key)
        {
            case "server-port": case "query.port": case "rcon.port": Number(value, 1, 65535, key); break;
            case "max-players": Number(value, 1, 100000, key); break;
            case "view-distance": case "simulation-distance": Number(value, 2, 32, key); break;
            case "difficulty": Choice(value, ["peaceful", "easy", "normal", "hard", "0", "1", "2", "3"], key); break;
            case "gamemode": Choice(value, ["survival", "creative", "adventure", "spectator", "0", "1", "2", "3"], key); break;
            case "level-name":
                if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(['/', '\\', '\0', '\n', '\r']) >= 0)
                    throw new ArgumentException("World name must be a single safe directory name.");
                break;
            case "white-list": case "online-mode": case "enable-rcon": case "enable-query": case "enforce-whitelist":
            case "pvp": case "allow-flight": case "spawn-animals": case "spawn-monsters":
                Choice(value, ["true", "false"], key); break;
        }
        if (value.Contains('\0')) throw new ArgumentException("Property values cannot contain null characters.");
    }
    private static void Number(string value, int min, int max, string key)
    { if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max) throw new ArgumentException($"{key} must be between {min} and {max}."); }
    private static void Choice(string value, string[] options, string key)
    { if (!options.Contains(value)) throw new ArgumentException($"{key} must be one of: {string.Join(", ", options)}."); }
    private static bool HasContinuation(string s)
    { var count = 0; for (var i = s.Length - 1; i >= 0 && s[i] == '\\'; i--) count++; return count % 2 != 0; }
    private static string Unescape(string s)
    {
        var b = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length) { b.Append(s[i]); continue; }
            var c = s[++i];
            if (c == 'u')
            {
                if (i + 4 >= s.Length || !ushort.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    throw new FormatException("Invalid Java properties Unicode escape.");
                b.Append((char)code); i += 4;
            }
            else b.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'f' => '\f', _ => c });
        }
        return b.ToString();
    }
    private static string Escape(string s, bool key)
    {
        var b = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\') b.Append("\\\\");
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') b.Append("\\r");
            else if (c == '\t') b.Append("\\t");
            else if (c == '\f') b.Append("\\f");
            else if ((key && c is '=' or ':' or '#' or '!' or ' ') || (!key && i == 0 && c == ' ')) b.Append('\\').Append(c);
            else if (c < 32 || c > 126) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else b.Append(c);
        }
        return b.ToString();
    }
}
