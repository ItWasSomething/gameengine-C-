using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Editor.Scripting;

public enum CompletionKind { Keyword, Type, Namespace, Method, Property, Field, Event, Local, EnumMember }

public sealed record CompletionItem(string Name, CompletionKind Kind, string Detail);

// Code intelligence for the Script Editor, using the same Roslyn compiler
// as the build — so suggestions and errors are exactly what the compiler
// thinks, not a guess:
//
//   Check    — errors/warnings for the text you're typing, before saving
//   Complete — what could go at the cursor: after "transform." the members
//              of Transform, otherwise everything in scope + C# keywords
//
// Both run on a background thread. Other script files' syntax trees are
// cached (keyed by file time), so each request only re-parses the file
// being edited.
public static class ScriptAnalysis
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (DateTime Time, SyntaxTree Tree)> FileTrees = new(StringComparer.OrdinalIgnoreCase);
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);
    private static SyntaxTree? _globalUsings;

    private static readonly string[] Keywords =
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "false", "finally", "float", "for", "foreach", "if", "in", "int",
        "interface", "internal", "is", "long", "new", "null", "out", "override", "private", "protected", "public",
        "readonly", "ref", "return", "sealed", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "using", "var", "virtual", "void", "while", "yield", "nameof", "async", "await", "get", "set",
    };

    private static CSharpCompilation Build(string assetsFolder, string currentFile, string currentText, out SyntaxTree currentTree)
    {
        lock (Lock)
        {
            _globalUsings ??= CSharpSyntaxTree.ParseText(
                SourceText.From(string.Join("\n", ScriptCompiler.GlobalUsings.Select(u => $"global using {u};")), System.Text.Encoding.UTF8),
                ParseOptions, path: "GlobalUsings.g.cs");

            var trees = new List<SyntaxTree> { _globalUsings };
            string current = Path.GetFullPath(currentFile);
            foreach (var file in Directory.Exists(assetsFolder)
                         ? Directory.GetFiles(assetsFolder, "*.cs", SearchOption.AllDirectories) : Array.Empty<string>())
            {
                string full = Path.GetFullPath(file);
                if (string.Equals(full, current, StringComparison.OrdinalIgnoreCase)) continue;
                var time = File.GetLastWriteTimeUtc(full);
                if (!FileTrees.TryGetValue(full, out var cached) || cached.Time != time)
                {
                    try { cached = (time, CSharpSyntaxTree.ParseText(File.ReadAllText(full), ParseOptions, path: full)); }
                    catch (IOException) { continue; }
                    FileTrees[full] = cached;
                }
                trees.Add(cached.Tree);
            }
            currentTree = CSharpSyntaxTree.ParseText(currentText, ParseOptions, path: current);
            trees.Add(currentTree);

            return CSharpCompilation.Create("Analysis", trees, ScriptCompiler.References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
        }
    }

    // ---- Live errors ----------------------------------------------------------

    public static List<ScriptDiagnostic> Check(string assetsFolder, string file, string text)
    {
        var compilation = Build(assetsFolder, file, text, out var tree);
        return compilation.GetSemanticModel(tree).GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new ScriptDiagnostic(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                    d.Id, d.GetMessage(), d.Severity == DiagnosticSeverity.Error,
                    span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
            })
            .ToList();
    }

    // ---- Completion ---------------------------------------------------------------

    // `position` = character offset of the cursor in `text`. `replaceStart`
    // returns where the word being completed begins.
    public static List<CompletionItem> Complete(string assetsFolder, string file, string text, int position, out int replaceStart)
    {
        int start = Math.Clamp(position, 0, text.Length);
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        replaceStart = start;

        var compilation = Build(assetsFolder, file, text, out var tree);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        // Inside a comment or string? Then nothing to suggest.
        var token = root.FindToken(Math.Max(0, start - 1));
        if (root.FindTrivia(Math.Max(0, start - 1)).IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            token.IsKind(SyntaxKind.StringLiteralToken) && token.Span.Contains(start))
            return new List<CompletionItem>();

        IEnumerable<ISymbol> symbols;
        bool afterDot = start > 0 && text[start - 1] == '.';
        if (afterDot)
        {
            var left = token.Parent switch
            {
                MemberAccessExpressionSyntax ma => (SyntaxNode)ma.Expression,
                QualifiedNameSyntax qn => qn.Left,
                _ => null,
            };
            if (left == null) return new List<CompletionItem>();

            var info = model.GetSymbolInfo(left);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is INamespaceSymbol ns)
                symbols = model.LookupNamespacesAndTypes(start, ns);
            else if (symbol is INamedTypeSymbol type)
                symbols = model.LookupStaticMembers(start, type);       // `Mathf.` / `Input.` / `Vector3.`
            else if (model.GetTypeInfo(left).Type is { } instanceType)
                symbols = model.LookupSymbols(start, instanceType, includeReducedExtensionMethods: true)
                    .Where(s => !s.IsStatic || s is IMethodSymbol { MethodKind: MethodKind.ReducedExtension });
            else
                return new List<CompletionItem>();
        }
        else
        {
            symbols = model.LookupSymbols(start);
        }

        var items = symbols
            .Where(s => s.CanBeReferencedByName && !s.IsImplicitlyDeclared && !s.Name.StartsWith('<'))
            .Where(s => s is not IMethodSymbol m || m.MethodKind is MethodKind.Ordinary or MethodKind.ReducedExtension)
            .GroupBy(s => s.Name)
            .Select(g => ToItem(g.First(), g.Count()))
            .ToList();

        if (!afterDot)
            items.AddRange(Keywords.Where(k => items.All(i => i.Name != k)).Select(k => new CompletionItem(k, CompletionKind.Keyword, "keyword")));
        return items;
    }

    private static CompletionItem ToItem(ISymbol s, int overloads)
    {
        var kind = s switch
        {
            INamespaceSymbol => CompletionKind.Namespace,
            INamedTypeSymbol => CompletionKind.Type,
            IMethodSymbol => CompletionKind.Method,
            IPropertySymbol => CompletionKind.Property,
            IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => CompletionKind.EnumMember,
            IFieldSymbol => CompletionKind.Field,
            IEventSymbol => CompletionKind.Event,
            _ => CompletionKind.Local,
        };
        string detail = s switch
        {
            IMethodSymbol m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + (overloads > 1 ? $"  (+{overloads - 1} overloads)" : ""),
            IPropertySymbol p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}",
            IFieldSymbol f => $"{f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {f.Name}",
            ILocalSymbol l => $"{l.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {l.Name}",
            IParameterSymbol p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}",
            INamedTypeSymbol t => $"{t.TypeKind.ToString().ToLowerInvariant()} {t.ToDisplayString()}",
            _ => s.ToDisplayString(),
        };
        return new CompletionItem(s.Name, kind, detail);
    }
}
