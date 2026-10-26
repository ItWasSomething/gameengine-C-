using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Editor.Scripting;

public sealed record ScriptDiagnostic(string File, int Line, int Column, string Code, string Message, bool IsError,
    int EndLine = 0, int EndColumn = 0)
{
    // "Assets/Scripts/Player.cs(12,5): error CS1002: ; expected" — the
    // standard compiler format, which IDEs and people both recognise.
    public string Format(string projectDir) =>
        $"{Path.GetRelativePath(projectDir, File).Replace('\\', '/')}({Line},{Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
}

public sealed record CompileResult(bool Success, byte[]? Assembly, byte[]? Symbols, IReadOnlyList<ScriptDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, string> TypeFiles, TimeSpan Duration);

// Compiles every .cs file under Assets/ into one in-memory assembly
// ("Assembly-CSharp", Unity's name for it) using Roslyn — the same C#
// compiler `dotnet build` uses, as a library. No project file, no build
// step, no disk output needed: source text in, DLL bytes out.
public static class ScriptCompiler
{
    public const string AssemblyName = "Assembly-CSharp";

    // Lines every script gets for free, so a script needs no `using`s for
    // the common stuff — like Unity, where `using UnityEngine;` is all you
    // ever need. Aliases pin down names that would otherwise be ambiguous.
    public static readonly string[] GlobalUsings =
    {
        "System", "System.Collections", "System.Collections.Generic", "System.Linq", "System.Numerics",
        "Engine.Core",
        "Input = Engine.Input",
        "Debug = Engine.Core.Debug",
        "Key = Silk.NET.Input.Key",
        "Random = System.Random",
    };

    // What scripts may reference: the .NET base library, the engine, and the
    // Silk.NET types the engine's API exposes (Key). Deliberately *not* the
    // editor, ImGui or Roslyn — game code must run without the editor.
    internal static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
        var allowed = tpa.Where(path =>
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith("System") || name == "netstandard" || name == "mscorlib" ||
                   name == "Microsoft.CSharp" || name == "Microsoft.Win32.Primitives" ||
                   name == "Engine" || name.StartsWith("Silk.NET.Input") || name.StartsWith("Silk.NET.Maths") ||
                   name == "Silk.NET.Core";
        });
        return allowed.Where(File.Exists).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    });

    public static CompileResult Compile(string assetsFolder)
    {
        var started = DateTime.Now;
        var files = Directory.Exists(assetsFolder)
            ? Directory.GetFiles(assetsFolder, "*.cs", SearchOption.AllDirectories)
            : Array.Empty<string>();

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(
                SourceText.From(string.Join("\n", GlobalUsings.Select(u => $"global using {u};")), System.Text.Encoding.UTF8),
                parseOptions, path: "GlobalUsings.g.cs"),
        };
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { Thread.Sleep(50); text = File.ReadAllText(file); }   // editor still writing it
            // Embedding the text lets exception stack traces and debuggers
            // show the source without needing the file at the same path.
            trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(text, System.Text.Encoding.UTF8), parseOptions, path: file));
        }

        var compilation = CSharpCompilation.Create(
            AssemblyName + "-" + Guid.NewGuid().ToString("N")[..8],   // unique name: old + new can coexist while swapping
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: false));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            embeddedTexts: trees.Skip(1).Select(t => EmbeddedText.FromSource(t.FilePath, t.GetText())));

        var diagnostics = emit.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            // Errors are always reported (even ones without a source location,
            // like a missing reference); warnings only for the user's own files.
            .Where(d => d.Severity == DiagnosticSeverity.Error ||
                        (d.Location.IsInSource && d.Location.SourceTree?.FilePath != "GlobalUsings.g.cs"))
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new ScriptDiagnostic(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                    d.Id, d.GetMessage(), d.Severity == DiagnosticSeverity.Error,
                    span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
            })
            .OrderByDescending(d => d.IsError).ThenBy(d => d.File).ThenBy(d => d.Line)
            .ToList();

        // Which file declares each class — used for "Edit Script".
        var typeFiles = new Dictionary<string, string>();
        foreach (var tree in trees.Skip(1))
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
                typeFiles.TryAdd(cls.Identifier.Text, tree.FilePath);

        return new CompileResult(emit.Success, emit.Success ? pe.ToArray() : null, emit.Success ? pdb.ToArray() : null,
            diagnostics, typeFiles, DateTime.Now - started);
    }
}

// Script assemblies are loaded into their own *collectible* load context,
// so after a recompile the previous version can be unloaded instead of
// piling up in memory forever.
public sealed class ScriptLoadContext : System.Runtime.Loader.AssemblyLoadContext
{
    public ScriptLoadContext() : base("Scripts", isCollectible: true) { }

    // Engine, Silk.NET etc. resolve to the copies the editor already has
    // loaded (returning null = "ask the default context").
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
