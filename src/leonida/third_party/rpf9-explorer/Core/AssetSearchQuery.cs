using System.Globalization;
using System.Text.RegularExpressions;

namespace RpfPreview.Core;


public sealed class AssetSearchQuery
{
    private sealed record Term(string Field, string Value, bool Exclude)
    {
        private Regex? _wildcard;
        public bool Contains(string source, string? value = null)
        {
            value ??= Value;
            if (!value.Contains('*') && !value.Contains('?')) return source.Contains(value, StringComparison.OrdinalIgnoreCase);
            _wildcard ??= new Regex(Regex.Escape(value).Replace("\\*", ".*").Replace("\\?", "."),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
            return _wildcard.IsMatch(source);
        }
    }
    private readonly Term[] _terms;
    private AssetSearchQuery(Term[] terms) => _terms = terms;
    public static AssetSearchQuery Parse(string text)
    {
        var terms = Regex.Matches(text ?? "", "(?:[^\\s\"]|\"[^\"]*\")+").Cast<Match>().Select(m => {
            string token = m.Value; bool exclude = token.StartsWith('-'); if (exclude) token = token[1..];
            int colon = token.IndexOf(':'); string field = colon > 0 ? token[..colon].ToLowerInvariant() : "";
            string value = (colon > 0 ? token[(colon + 1)..] : token).Trim('"');
            return new Term(field, value, exclude);
        }).Where(t => t.Value.Length > 0).ToArray();
        return new(terms);
    }

    public bool Matches(WorkspaceAsset asset)
    {
        foreach (var term in _terms)
        {
            bool match = term.Field switch {
                "type" => TypeMatches(asset.Kind, term.Value),
                "name" => term.Contains(asset.Name),
                "source" or "archive" => term.Contains(asset.SourcePath),
                "format" => term.Contains(asset.Format),
                "key" or "hash" => term.Contains(asset.KeyText, term.Value.Replace("0x", "", StringComparison.OrdinalIgnoreCase)),
                "width" or "w" => Number(asset.Width, term.Value),
                "height" or "h" => Number(asset.Height, term.Value),
                "mips" => Number(asset.Mips, term.Value),
                "size" => Number(asset.Size, term.Value),
                _ => term.Contains(asset.SearchText, term.Field.Length == 0 ? term.Value : term.Field + ":" + term.Value)
            };
            if (term.Exclude ? match : !match) return false;
        }
        return true;
    }
    public static bool TypeMatches(string kind, string value) => value.ToLowerInvariant() switch {
        "images" or "textures" or "texture" or "image" => kind == "image",
        "models" or "model" => kind == "model", "sounds" or "audio" => kind == "audio",
        "shaders" or "shader" => kind == "shader", "containers" or "dictionary" => kind == "dictionary",
        "all" => true, _ => string.Equals(kind, value, StringComparison.OrdinalIgnoreCase)
    };
    private static bool Number(long actual, string expression)
    {
        string op = expression.StartsWith(">=") || expression.StartsWith("<=") ? expression[..2] : expression.StartsWith('>') || expression.StartsWith('<') || expression.StartsWith('=') ? expression[..1] : "=";
        int start = op == "=" && !expression.StartsWith('=') ? 0 : op.Length;
        string value = expression[start..].ToLowerInvariant();
        double scale = 1;
        foreach (var suffix in new[] { "gib", "gb", "mib", "mb", "kib", "kb", "b" })
            if (value.EndsWith(suffix, StringComparison.Ordinal)) { scale = suffix[0] switch { 'g' => 1073741824, 'm' => 1048576, 'k' => 1024, _ => 1 }; value = value[..^suffix.Length]; break; }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed) || parsed < 0) return false;
        double expected = parsed * scale;
        return op switch { ">=" => actual >= expected, "<=" => actual <= expected, ">" => actual > expected, "<" => actual < expected, _ => actual == expected };
    }
}
