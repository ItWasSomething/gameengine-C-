namespace Editor.Code;

public enum TokenKind { Text, Keyword, ControlKeyword, BuiltinType, Type, Method, Number, String, Comment, Preprocessor, Punctuation, Field }

public readonly record struct Token(int Start, int Length, TokenKind Kind);

// What a line starts "inside of" — needed because some tokens span lines.
public enum LineState { Normal, BlockComment, VerbatimString, RawString }

// A small hand-written C# lexer for coloring. It doesn't understand the
// language (that's Roslyn's job, for errors and autocomplete) — it just
// splits each line into comments, strings, numbers, keywords and names,
// fast enough to run on every edit.
//
// Names are colored by a few cheap rules: known engine/script/.NET type
// names → Type; a name followed by "(" → Method; a name after "class",
// "new", "struct"... → Type; a name starting with "_" → Field.
public static class SyntaxHighlighter
{
    private static readonly HashSet<string> Keywords = new()
    {
        "abstract", "as", "base", "class", "const", "default", "delegate", "enum", "event", "explicit", "extern",
        "false", "fixed", "implicit", "in", "interface", "internal", "is", "lock", "namespace", "new", "null",
        "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "sealed",
        "sizeof", "stackalloc", "static", "struct", "this", "true", "typeof", "unsafe", "using", "virtual",
        "volatile", "nameof", "get", "set", "init", "value", "var", "async", "await", "record", "required", "global",
        "where", "with", "and", "or", "not",
    };

    private static readonly HashSet<string> ControlKeywords = new()
    {
        "if", "else", "switch", "case", "for", "foreach", "while", "do", "break", "continue", "return", "goto",
        "try", "catch", "finally", "throw", "yield", "when",
    };

    private static readonly HashSet<string> BuiltinTypes = new()
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short",
        "ushort", "object", "string", "void", "dynamic", "nint", "nuint",
    };

    // Keywords after which the next name is a type name.
    private static readonly HashSet<string> TypeIntroducers = new() { "class", "struct", "enum", "interface", "new", "record", "is", "as", "typeof" };

    public static LineState Tokenize(string line, LineState state, ISet<string> knownTypes, List<Token> output)
    {
        output.Clear();
        int i = 0;
        string? previousWord = null;

        while (i < line.Length)
        {
            switch (state)
            {
                case LineState.BlockComment:
                {
                    int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    int stop = end < 0 ? line.Length : end + 2;
                    output.Add(new Token(i, stop - i, TokenKind.Comment));
                    i = stop;
                    if (end >= 0) state = LineState.Normal;
                    continue;
                }
                case LineState.VerbatimString:
                {
                    int j = i;
                    while (j < line.Length)
                    {
                        if (line[j] == '"')
                        {
                            if (j + 1 < line.Length && line[j + 1] == '"') { j += 2; continue; }   // "" = escaped quote
                            j++;
                            state = LineState.Normal;
                            break;
                        }
                        j++;
                    }
                    output.Add(new Token(i, j - i, TokenKind.String));
                    i = j;
                    continue;
                }
                case LineState.RawString:
                {
                    int end = line.IndexOf("\"\"\"", i, StringComparison.Ordinal);
                    int stop = end < 0 ? line.Length : end + 3;
                    output.Add(new Token(i, stop - i, TokenKind.String));
                    i = stop;
                    if (end >= 0) state = LineState.Normal;
                    continue;
                }
            }

            char c = line[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Comments.
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                output.Add(new Token(i, line.Length - i, TokenKind.Comment));
                break;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                state = LineState.BlockComment;
                continue;
            }

            // #region, #if ... (only at the start of a line).
            if (c == '#' && line[..i].Trim().Length == 0)
            {
                output.Add(new Token(i, line.Length - i, TokenKind.Preprocessor));
                break;
            }

            // Strings: """raw""", @"verbatim", $"interpolated", "normal".
            if (c == '"' && i + 2 < line.Length && line[i + 1] == '"' && line[i + 2] == '"' ||
                (c == '$' && i + 3 < line.Length && line.AsSpan(i + 1).StartsWith("\"\"\"")))
            {
                int open = c == '$' ? i + 1 : i;
                int end = line.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
                if (end < 0) { output.Add(new Token(i, line.Length - i, TokenKind.String)); state = LineState.RawString; break; }
                output.Add(new Token(i, end + 3 - i, TokenKind.String));
                i = end + 3;
                continue;
            }
            int prefix = StringPrefixLength(line, i);
            if (prefix >= 0)
            {
                bool verbatim = line.AsSpan(i, prefix).Contains('@');
                int j = i + prefix + 1;   // past the opening quote
                if (verbatim)
                {
                    output.Add(new Token(i, prefix + 1, TokenKind.String));
                    i = j;
                    state = LineState.VerbatimString;
                    continue;
                }
                while (j < line.Length && line[j] != '"')
                    j += line[j] == '\\' ? 2 : 1;
                j = Math.Min(j + 1, line.Length);
                output.Add(new Token(i, j - i, TokenKind.String));
                i = j;
                continue;
            }
            if (c == '\'')
            {
                int j = i + 1;
                while (j < line.Length && line[j] != '\'') j += line[j] == '\\' ? 2 : 1;
                j = Math.Min(j + 1, line.Length);
                output.Add(new Token(i, j - i, TokenKind.String));
                i = j;
                continue;
            }

            // Numbers: 42, 3.5f, 0xFF, 1_000.
            if (char.IsDigit(c) || (c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                int j = i + 1;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '.' || line[j] == '_'))
                {
                    if (line[j] == '.' && (j + 1 >= line.Length || !char.IsDigit(line[j + 1]))) break;
                    j++;
                }
                output.Add(new Token(i, j - i, TokenKind.Number));
                i = j;
                continue;
            }

            // Names and keywords.
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                int j = i + 1;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                string word = line[i..j];
                output.Add(new Token(i, j - i, Classify(word, line, j, previousWord, knownTypes)));
                previousWord = word;
                i = j;
                continue;
            }

            output.Add(new Token(i, 1, TokenKind.Punctuation));
            if (c != '.' && c != '<' && c != ',' && c != '>') previousWord = null;
            i++;
        }
        return state;
    }

    // Length of a string's prefix before the opening quote ("" → 0, $" → 1,
    // @$" → 2), or -1 if this isn't the start of a string.
    private static int StringPrefixLength(string line, int i)
    {
        int j = i;
        while (j < line.Length && j - i < 2 && (line[j] == '$' || line[j] == '@')) j++;
        return j < line.Length && line[j] == '"' ? j - i : -1;
    }

    private static TokenKind Classify(string word, string line, int end, string? previousWord, ISet<string> knownTypes)
    {
        if (ControlKeywords.Contains(word)) return TokenKind.ControlKeyword;
        if (BuiltinTypes.Contains(word)) return TokenKind.BuiltinType;
        if (Keywords.Contains(word)) return TokenKind.Keyword;
        if (knownTypes.Contains(word)) return TokenKind.Type;
        if (previousWord != null && TypeIntroducers.Contains(previousWord) && char.IsUpper(word[0])) return TokenKind.Type;

        int k = end;
        while (k < line.Length && line[k] == ' ') k++;
        if (k < line.Length && line[k] == '(') return TokenKind.Method;
        if (word[0] == '_') return TokenKind.Field;
        return TokenKind.Text;
    }
}
