using System.Numerics;
using System.Text;
using Editor.Scripting;
using ImGuiNET;

namespace Editor.Code;

public readonly record struct TextPos(int Line, int Col) : IComparable<TextPos>
{
    public int CompareTo(TextPos other) => Line != other.Line ? Line.CompareTo(other.Line) : Col.CompareTo(other.Col);
    public static bool operator <(TextPos a, TextPos b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPos a, TextPos b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPos a, TextPos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPos a, TextPos b) => a.CompareTo(b) >= 0;
}

// An error or warning to show in the text. Lines and columns are 0-based.
public sealed record EditorMarker(int Line, int Col, int EndLine, int EndCol, string Message, bool IsError);

// A code-editing widget built from scratch on ImGui's draw list, because
// ImGui's own multiline text box can't color text, draw a gutter, or
// underline errors. It owns everything a text box normally hides:
//
//   - the text, as a list of lines (tabs become 4 spaces on the way in)
//   - a cursor + selection anchor, as (line, column) positions
//   - keyboard and mouse handling, clipboard, undo/redo
//   - drawing: gutter, current line, selection, colored tokens, squiggles,
//     bracket matching, caret
//   - autocomplete popup and find/replace bar
//
// Positions are in characters; the font is monospaced, so "column 12" is
// always 12 × (width of one character) from the left — that's what makes
// mouse picking and drawing simple.
public sealed class CodeEditor
{
    private const int TabSize = 4;

    // ---- Colors (monochrome) -------------------------------------------------
    private static readonly Dictionary<TokenKind, uint> TokenColors = new()
    {
        [TokenKind.Text] = Col(0xD4D4D4),
        [TokenKind.Keyword] = Col(0xFFFFFF),
        [TokenKind.ControlKeyword] = Col(0xFFFFFF),
        [TokenKind.BuiltinType] = Col(0xEDEDED),
        [TokenKind.Type] = Col(0xE6E6E6),
        [TokenKind.Method] = Col(0xF0F0F0),
        [TokenKind.Number] = Col(0xB8B8B8),
        [TokenKind.String] = Col(0x9E9E9E),
        [TokenKind.Comment] = Col(0x5F5F5F),
        [TokenKind.Preprocessor] = Col(0x7A7A7A),
        [TokenKind.Punctuation] = Col(0x8A8A8A),
        [TokenKind.Field] = Col(0xC4C4C4),
    };
    private static readonly uint BackgroundColor = Col(0x0B0B0B);
    private static readonly uint GutterColor = Col(0x080808);
    private static readonly uint LineNumberColor = Col(0x4A4A4A);
    private static readonly uint CurrentLineNumberColor = Col(0xFFFFFF);
    private static readonly uint CurrentLineColor = Col(0x161616);
    private static readonly uint SelectionColor = Col(0xFFFFFF, 0.22f);
    private static readonly uint FindMatchColor = Col(0xFFFFFF, 0.14f);
    private static readonly uint CaretColor = Col(0xFFFFFF);
    private static readonly uint ErrorColor = Col(0xFF5C5C);
    private static readonly uint WarningColor = Col(0xE8B04A);
    private static readonly uint BracketColor = Col(0xFFFFFF, 0.6f);

    private static uint Col(uint rgb, float alpha = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha));

    // ---- State ------------------------------------------------------------------

    private static int _focusedFrame = -10;

    // True while any code editor has keyboard focus — the app checks this so
    // its own shortcuts (Delete, Ctrl+D, W/E/R...) don't fire while typing.
    public static bool AnyFocused => ImGui.GetFrameCount() - _focusedFrame <= 1;

    private List<string> _lines = new() { "" };
    private TextPos _cursor, _anchor;
    private bool _focused;
    private bool _dragging;
    private float _blink;
    private bool _scrollToCursor;
    private int? _centerOnLine;
    private int _preferredCol = -1;   // keeps the column when moving up/down through short lines

    public int Version { get; private set; }
    public TextPos Cursor => _cursor;
    public bool HasSelection => _cursor != _anchor;

    public ISet<string> KnownTypes { get; set; } = new HashSet<string>();
    public IReadOnlyList<EditorMarker> Markers { get; set; } = Array.Empty<EditorMarker>();

    // (text, cursor offset) → suggestions + where the word being completed starts.
    // Called on a background thread.
    public Func<string, int, (List<CompletionItem> Items, int ReplaceStart)>? CompletionProvider { get; set; }

    // ---- Undo ---------------------------------------------------------------------

    private sealed record Snapshot(string Text, TextPos Cursor, TextPos Anchor);
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    private string _lastEditKind = "";
    private DateTime _lastEditTime;

    // ---- Highlighting cache ---------------------------------------------------------

    private int _tokensVersion = -1;
    private readonly List<List<Token>> _lineTokens = new();
    private int _maxLineLength;

    // ---- Autocomplete -----------------------------------------------------------------

    private Task<(List<CompletionItem> Items, int ReplaceStart)>? _completionTask;
    private List<CompletionItem> _completionAll = new();
    private List<CompletionItem> _completionShown = new();
    private TextPos _completionStart;
    private bool _completionOpen;
    private int _completionIndex;
    private Vector2 _popupMin, _popupMax;

    // ---- Find / go to line -------------------------------------------------------------

    private bool _findOpen, _findFocus, _replaceOpen, _matchCase;
    private string _findText = "", _replaceText = "";
    private List<TextPos> _findMatches = new();
    private int _findMatchesVersion = -1;
    private string _findMatchesQuery = "";
    private bool _gotoOpen;
    private string _gotoText = "";
    private Vector2 _findBarMin, _findBarMax;

    // =========================================================================
    //  Text access
    // =========================================================================

    public string Text => string.Join("\n", _lines);

    public void SetText(string text)
    {
        _lines = Normalize(text).Split('\n').ToList();
        _cursor = _anchor = new TextPos(0, 0);
        _undo.Clear();
        _redo.Clear();
        Version++;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", new string(' ', TabSize));

    public void Focus() => _focused = true;

    public void GoToLine(int line1Based, int col1Based = 1)
    {
        int line = Math.Clamp(line1Based - 1, 0, _lines.Count - 1);
        _cursor = _anchor = new TextPos(line, Math.Clamp(col1Based - 1, 0, _lines[line].Length));
        _centerOnLine = line;
        _focused = true;
    }

    private int Offset(TextPos p)
    {
        int offset = 0;
        for (int i = 0; i < p.Line; i++) offset += _lines[i].Length + 1;
        return offset + p.Col;
    }

    private TextPos FromOffset(int offset)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            if (offset <= _lines[i].Length) return new TextPos(i, offset);
            offset -= _lines[i].Length + 1;
        }
        return new TextPos(_lines.Count - 1, _lines[^1].Length);
    }

    private TextPos Clamp(TextPos p)
    {
        int line = Math.Clamp(p.Line, 0, _lines.Count - 1);
        return new TextPos(line, Math.Clamp(p.Col, 0, _lines[line].Length));
    }

    private (TextPos Start, TextPos End) Selection => _cursor < _anchor ? (_cursor, _anchor) : (_anchor, _cursor);

    private string GetRange(TextPos a, TextPos b)
    {
        if (a.Line == b.Line) return _lines[a.Line][a.Col..b.Col];
        var sb = new StringBuilder(_lines[a.Line][a.Col..]);
        for (int i = a.Line + 1; i < b.Line; i++) sb.Append('\n').Append(_lines[i]);
        sb.Append('\n').Append(_lines[b.Line][..b.Col]);
        return sb.ToString();
    }

    // =========================================================================
    //  Editing primitives — everything that changes text goes through these
    // =========================================================================

    private void PushUndo(string kind)
    {
        // Consecutive typing within a second or so is one undo step, like
        // every other editor; anything else starts a new step.
        bool coalesce = kind == "type" && _lastEditKind == "type" && (DateTime.Now - _lastEditTime).TotalSeconds < 1.2;
        if (!coalesce)
        {
            _undo.Add(new Snapshot(Text, _cursor, _anchor));
            if (_undo.Count > 300) _undo.RemoveAt(0);
            _redo.Clear();
        }
        _lastEditKind = kind;
        _lastEditTime = DateTime.Now;
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(new Snapshot(Text, _cursor, _anchor));
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(new Snapshot(Text, _cursor, _anchor));
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
    }

    private void Restore(Snapshot s)
    {
        _lines = s.Text.Split('\n').ToList();
        _cursor = Clamp(s.Cursor);
        _anchor = Clamp(s.Anchor);
        _lastEditKind = "";
        Changed();
    }

    private void Changed()
    {
        Version++;
        _blink = 0;
        _scrollToCursor = true;
    }

    private void DeleteRange(TextPos a, TextPos b)
    {
        if (a == b) return;
        string head = _lines[a.Line][..a.Col];
        string tail = _lines[b.Line][b.Col..];
        _lines.RemoveRange(a.Line + 1, b.Line - a.Line);
        _lines[a.Line] = head + tail;
        _cursor = _anchor = a;
        Changed();
    }

    private void Insert(string text)
    {
        if (HasSelection) { var (s, e) = Selection; DeleteRange(s, e); }
        var parts = Normalize(text).Split('\n');
        string line = _lines[_cursor.Line];
        string head = line[.._cursor.Col], tail = line[_cursor.Col..];
        if (parts.Length == 1)
        {
            _lines[_cursor.Line] = head + parts[0] + tail;
            _cursor = new TextPos(_cursor.Line, _cursor.Col + parts[0].Length);
        }
        else
        {
            _lines[_cursor.Line] = head + parts[0];
            var middle = parts[1..^1];
            _lines.InsertRange(_cursor.Line + 1, middle);
            int lastLine = _cursor.Line + parts.Length - 1;
            _lines.Insert(lastLine, parts[^1] + tail);
            _cursor = new TextPos(lastLine, parts[^1].Length);
        }
        _anchor = _cursor;
        Changed();
    }

    private void MoveCursor(TextPos p, bool select)
    {
        _cursor = Clamp(p);
        if (!select) _anchor = _cursor;
        _blink = 0;
        _scrollToCursor = true;
    }

    private static int Indentation(string line) => line.Length - line.TrimStart(' ').Length;

    // =========================================================================
    //  Draw (and input — handled first so drawing shows this frame's state)
    // =========================================================================

    public void Draw(string id, Vector2 size, ImFontPtr font)
    {
        if (_focused) _focusedFrame = ImGui.GetFrameCount();
        PollCompletion();

        if (_findOpen || _gotoOpen) DrawFindBar(font);

        ImGui.PushFont(font);
        float lineHeight = ImGui.GetTextLineHeight() + 2;
        float charWidth = ImGui.CalcTextSize("M").X;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BackgroundColor);
        ImGui.BeginChild(id, size, ImGuiChildFlags.None,
            ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoNavInputs | ImGuiWindowFlags.NoMove);

        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        float scrollX = ImGui.GetScrollX(), scrollY = ImGui.GetScrollY();
        int digits = Math.Max(3, _lines.Count.ToString().Length);
        float gutterWidth = charWidth * digits + 26;
        var origin = ImGui.GetCursorScreenPos();          // top-left of the (scrolled) content
        float textX = origin.X + gutterWidth + 6;

        UpdateTokens();
        // Reserve the full content size so ImGui gives us scrollbars; allow
        // scrolling half a page past the last line, like most code editors.
        ImGui.Dummy(new Vector2(gutterWidth + 6 + _maxLineLength * charWidth + 60, _lines.Count * lineHeight + winSize.Y * 0.5f));

        HandleMouse(origin, textX, lineHeight, charWidth, winPos, winSize);
        if (_focused) HandleKeyboard(winSize, lineHeight);
        ContextMenu();

        // ---- Draw ----
        // Input above may have just changed the text (Enter splits a line,
        // Backspace joins two...). Re-tokenize before drawing, or the token
        // ranges from the old text can point past the end of a shorter line.
        UpdateTokens();
        var dl = ImGui.GetWindowDrawList();
        int first = Math.Max(0, (int)(scrollY / lineHeight));
        int last = Math.Min(_lines.Count - 1, first + (int)(winSize.Y / lineHeight) + 1);
        var (selStart, selEnd) = Selection;
        var markersByLine = Markers.GroupBy(m => Math.Clamp(m.Line, 0, _lines.Count - 1)).ToDictionary(g => g.Key, g => g.ToList());
        var bracket = FindMatchingBracket();
        UpdateFindMatches();

        for (int i = first; i <= last; i++)
        {
            float y = origin.Y + i * lineHeight;
            string line = _lines[i];

            if (i == _cursor.Line && !HasSelection)
                dl.AddRectFilled(new Vector2(winPos.X, y), new Vector2(winPos.X + winSize.X, y + lineHeight), CurrentLineColor);
            if (markersByLine.TryGetValue(i, out var lineMarkers))
            {
                uint tint = lineMarkers.Any(m => m.IsError) ? Col(0xF2545B, 0.07f) : Col(0xF2C14E, 0.05f);
                dl.AddRectFilled(new Vector2(winPos.X, y), new Vector2(winPos.X + winSize.X, y + lineHeight), tint);
            }

            // Find matches.
            if (_findOpen && _findText.Length > 0)
                foreach (var m in _findMatches.Where(m => m.Line == i))
                    dl.AddRectFilled(new Vector2(textX + m.Col * charWidth, y), new Vector2(textX + (m.Col + _findText.Length) * charWidth, y + lineHeight), FindMatchColor, 2);

            // Selection.
            if (HasSelection && i >= selStart.Line && i <= selEnd.Line)
            {
                int a = i == selStart.Line ? selStart.Col : 0;
                int b = i == selEnd.Line ? selEnd.Col : line.Length + 1;   // +1 shows the newline is selected
                dl.AddRectFilled(new Vector2(textX + a * charWidth, y), new Vector2(textX + b * charWidth, y + lineHeight), SelectionColor);
            }

            // Text, token by token.
            foreach (var t in _lineTokens[i])
            {
                if (t.Start >= line.Length) break;   // belt and braces: never index past the line
                dl.AddText(new Vector2(textX + t.Start * charWidth, y + 1), TokenColors[t.Kind], line.Substring(t.Start, Math.Min(t.Length, line.Length - t.Start)));
            }

            // Squiggles under errors/warnings.
            if (lineMarkers != null)
                foreach (var m in lineMarkers) DrawSquiggle(dl, m, i, line, textX, y + lineHeight - 2, charWidth);
        }

        foreach (var pos in bracket)
        {
            float y = origin.Y + pos.Line * lineHeight;
            dl.AddRect(new Vector2(textX + pos.Col * charWidth - 1, y), new Vector2(textX + (pos.Col + 1) * charWidth + 1, y + lineHeight), BracketColor, 2);
        }

        // Caret: solid while typing, blinking when idle.
        _blink += ImGui.GetIO().DeltaTime;
        if (_focused && (_blink % 1.06f) < 0.6f)
        {
            float cx = textX + _cursor.Col * charWidth;
            float cy = origin.Y + _cursor.Line * lineHeight;
            dl.AddRectFilled(new Vector2(cx - 1, cy + 1), new Vector2(cx + 1, cy + lineHeight - 1), CaretColor);
        }

        DrawGutter(dl, winPos, winSize, origin, first, last, lineHeight, gutterWidth, markersByLine);
        HoverTooltips(origin, textX, lineHeight, charWidth, winPos, gutterWidth);
        ApplyScrolling(winSize, lineHeight, charWidth, gutterWidth, scrollX, scrollY);

        var caretScreen = new Vector2(textX + _completionStart.Col * charWidth, origin.Y + (_cursor.Line + 1) * lineHeight);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        if (_completionOpen) DrawCompletionPopup(caretScreen);
        ImGui.PopFont();
    }

    private void UpdateTokens()
    {
        if (_tokensVersion == Version) return;
        _tokensVersion = Version;
        _lineTokens.Clear();
        _maxLineLength = 0;
        var state = LineState.Normal;
        foreach (var line in _lines)
        {
            var tokens = new List<Token>();
            state = SyntaxHighlighter.Tokenize(line, state, KnownTypes, tokens);
            _lineTokens.Add(tokens);
            _maxLineLength = Math.Max(_maxLineLength, line.Length);
        }
    }

    // A wavy underline from the marker's start to its end on this line. A
    // zero-width marker (e.g. "; expected") underlines the character there.
    private void DrawSquiggle(ImDrawListPtr dl, EditorMarker m, int lineIndex, string line, float textX, float y, float charWidth)
    {
        int a = lineIndex == m.Line ? m.Col : 0;
        int b = lineIndex == m.EndLine ? m.EndCol : line.Length;
        if (b <= a) { a = Math.Max(0, Math.Min(a, line.Length - 1)); b = a + 1; }
        float x0 = textX + a * charWidth, x1 = textX + b * charWidth;
        uint color = m.IsError ? ErrorColor : WarningColor;
        var points = new List<Vector2>();
        bool up = true;
        for (float x = x0; x <= x1; x += 2.5f, up = !up) points.Add(new Vector2(x, y + (up ? -1.5f : 1.5f)));
        for (int i = 1; i < points.Count; i++) dl.AddLine(points[i - 1], points[i], color, 1.2f);
    }

    private void DrawGutter(ImDrawListPtr dl, Vector2 winPos, Vector2 winSize, Vector2 origin, int first, int last,
        float lineHeight, float gutterWidth, Dictionary<int, List<EditorMarker>> markers)
    {
        // Pinned to the left edge: it doesn't scroll sideways with the text.
        dl.AddRectFilled(winPos, new Vector2(winPos.X + gutterWidth, winPos.Y + winSize.Y), GutterColor);
        dl.AddLine(new Vector2(winPos.X + gutterWidth, winPos.Y), new Vector2(winPos.X + gutterWidth, winPos.Y + winSize.Y), Col(0x222222));
        for (int i = first; i <= last; i++)
        {
            float y = origin.Y + i * lineHeight;
            string number = (i + 1).ToString();
            float w = ImGui.CalcTextSize(number).X;
            dl.AddText(new Vector2(winPos.X + gutterWidth - w - 6, y + 1), i == _cursor.Line ? CurrentLineNumberColor : LineNumberColor, number);
            if (markers.TryGetValue(i, out var ms))
            {
                bool error = ms.Any(m => m.IsError);
                var c = new Vector2(winPos.X + 9, y + lineHeight / 2);
                dl.AddCircleFilled(c, 4.5f, error ? ErrorColor : WarningColor);
                // A tiny "!" in the dot.
                dl.AddLine(c - new Vector2(0, 2.5f), c + new Vector2(0, 0.5f), Col(0x0B0B0B), 1.5f);
                dl.AddCircleFilled(c + new Vector2(0, 2.2f), 0.8f, Col(0x0B0B0B));
            }
        }
    }

    private void HoverTooltips(Vector2 origin, float textX, float lineHeight, float charWidth, Vector2 winPos, float gutterWidth)
    {
        if (!ImGui.IsWindowHovered() || Markers.Count == 0) return;
        var mouse = ImGui.GetMousePos();
        int line = (int)((mouse.Y - origin.Y) / lineHeight);
        if (line < 0 || line >= _lines.Count) return;
        bool inGutter = mouse.X < winPos.X + gutterWidth;
        int col = (int)((mouse.X - textX) / charWidth);
        var hits = Markers.Where(m => m.Line == line && (inGutter ||
            (col >= m.Col - 1 && col <= Math.Max(m.EndLine == line ? m.EndCol : _lines[line].Length, m.Col + 1)))).ToList();
        if (hits.Count == 0) return;
        ImGui.BeginTooltip();
        foreach (var m in hits)
            ImGui.TextColored(m.IsError ? new Vector4(0.95f, 0.33f, 0.36f, 1) : new Vector4(0.95f, 0.76f, 0.31f, 1),
                (m.IsError ? "error  " : "warning  ") + m.Message);
        ImGui.EndTooltip();
    }

    private void ApplyScrolling(Vector2 winSize, float lineHeight, float charWidth, float gutterWidth, float scrollX, float scrollY)
    {
        if (_centerOnLine is { } center)
        {
            ImGui.SetScrollY(MathF.Max(0, center * lineHeight - winSize.Y / 2));
            _centerOnLine = null;
            _scrollToCursor = false;
            return;
        }
        if (!_scrollToCursor) return;
        _scrollToCursor = false;
        float y = _cursor.Line * lineHeight;
        if (y < scrollY) ImGui.SetScrollY(y);
        else if (y + lineHeight * 2 > scrollY + winSize.Y) ImGui.SetScrollY(y + lineHeight * 2 - winSize.Y);
        float x = _cursor.Col * charWidth;
        float visible = winSize.X - gutterWidth - 30;
        if (x < scrollX) ImGui.SetScrollX(MathF.Max(0, x - 40));
        else if (x > scrollX + visible) ImGui.SetScrollX(x - visible + 40);
    }

    // =========================================================================
    //  Mouse
    // =========================================================================

    private void HandleMouse(Vector2 origin, float textX, float lineHeight, float charWidth, Vector2 winPos, Vector2 winSize)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        bool hovered = ImGui.IsWindowHovered();
        bool overPopup = _completionOpen && mouse.X >= _popupMin.X && mouse.Y >= _popupMin.Y && mouse.X <= _popupMax.X && mouse.Y <= _popupMax.Y;
        if (hovered && !overPopup) ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);

        TextPos PosAtMouse()
        {
            int line = Math.Clamp((int)((mouse.Y - origin.Y) / lineHeight), 0, _lines.Count - 1);
            int col = Math.Clamp((int)MathF.Round((mouse.X - textX) / charWidth), 0, _lines[line].Length);
            return new TextPos(line, col);
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !overPopup)
        {
            if (hovered)
            {
                _focused = true;
                CloseCompletion();
                var pos = PosAtMouse();
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    var (s, e) = WordAt(pos);
                    _anchor = s;
                    _cursor = e;
                }
                else
                {
                    MoveCursor(pos, io.KeyShift);
                    _dragging = true;
                }
                _preferredCol = -1;
            }
            else _focused = false;   // clicked somewhere else in the editor UI
        }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && hovered)
        {
            _focused = true;
            var pos = PosAtMouse();
            var (s, e) = Selection;
            if (!HasSelection || pos < s || pos > e) MoveCursor(pos, false);
        }

        if (_dragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _cursor = PosAtMouse();
                // Dragging past the top/bottom edge scrolls.
                if (mouse.Y < winPos.Y + 4) ImGui.SetScrollY(ImGui.GetScrollY() - lineHeight);
                if (mouse.Y > winPos.Y + winSize.Y - 4) ImGui.SetScrollY(ImGui.GetScrollY() + lineHeight);
            }
            else _dragging = false;
        }
    }

    private (TextPos, TextPos) WordAt(TextPos p)
    {
        string line = _lines[p.Line];
        if (line.Length == 0) return (p, p);
        int i = Math.Min(p.Col, line.Length - 1);
        bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        bool kind = Word(line[i]);
        int s = i, e = i;
        while (s > 0 && Word(line[s - 1]) == kind && !char.IsWhiteSpace(line[s - 1])) s--;
        while (e < line.Length && Word(line[e]) == kind && !char.IsWhiteSpace(line[e])) e++;
        return (new TextPos(p.Line, s), new TextPos(p.Line, e));
    }

    private void ContextMenu()
    {
        if (!ImGui.BeginPopupContextWindow("codeContext")) return;
        if (ImGui.MenuItem("Cut", "Ctrl+X")) Cut();
        if (ImGui.MenuItem("Copy", "Ctrl+C")) Copy();
        if (ImGui.MenuItem("Paste", "Ctrl+V")) Paste();
        ImGui.Separator();
        if (ImGui.MenuItem("Select All", "Ctrl+A")) SelectAll();
        if (ImGui.MenuItem("Toggle Comment", "Ctrl+/")) ToggleComment();
        if (ImGui.MenuItem("Duplicate Line", "Ctrl+D")) DuplicateLines();
        ImGui.Separator();
        if (ImGui.MenuItem("Find / Replace", "Ctrl+F")) OpenFind(replace: true);
        if (ImGui.MenuItem("Go to Line", "Ctrl+G")) { _gotoOpen = true; _findFocus = true; _focused = false; }
        if (ImGui.MenuItem("Suggest", "Ctrl+Space")) RequestCompletion();
        ImGui.EndPopup();
    }

    // =========================================================================
    //  Keyboard
    // =========================================================================

    private static bool Pressed(ImGuiKey key) => ImGui.IsKeyPressed(key, true);

    private void HandleKeyboard(Vector2 winSize, float lineHeight)
    {
        var io = ImGui.GetIO();
        bool ctrl = io.KeyCtrl, shift = io.KeyShift, alt = io.KeyAlt;

        // The completion popup gets first say over navigation keys.
        if (_completionOpen)
        {
            if (Pressed(ImGuiKey.DownArrow)) { _completionIndex = Math.Min(_completionIndex + 1, _completionShown.Count - 1); return; }
            if (Pressed(ImGuiKey.UpArrow)) { _completionIndex = Math.Max(_completionIndex - 1, 0); return; }
            if (Pressed(ImGuiKey.PageDown)) { _completionIndex = Math.Min(_completionIndex + 8, _completionShown.Count - 1); return; }
            if (Pressed(ImGuiKey.PageUp)) { _completionIndex = Math.Max(_completionIndex - 8, 0); return; }
            if (Pressed(ImGuiKey.Enter) || Pressed(ImGuiKey.Tab) || Pressed(ImGuiKey.KeypadEnter)) { AcceptCompletion(); return; }
            if (Pressed(ImGuiKey.Escape)) { CloseCompletion(); return; }
        }

        int pageLines = Math.Max(1, (int)(winSize.Y / lineHeight) - 2);

        // ---- Shortcuts with Ctrl ----
        if (ctrl && !alt)
        {
            if (Pressed(ImGuiKey.A)) { SelectAll(); return; }
            if (Pressed(ImGuiKey.C)) { Copy(); return; }
            if (Pressed(ImGuiKey.X)) { Cut(); return; }
            if (Pressed(ImGuiKey.V)) { Paste(); return; }
            if (Pressed(ImGuiKey.Z)) { if (shift) Redo(); else Undo(); return; }
            if (Pressed(ImGuiKey.Y)) { Redo(); return; }
            if (Pressed(ImGuiKey.D)) { DuplicateLines(); return; }
            if (Pressed(ImGuiKey.Slash)) { ToggleComment(); return; }
            if (Pressed(ImGuiKey.F)) { OpenFind(replace: false); return; }
            if (Pressed(ImGuiKey.H)) { OpenFind(replace: true); return; }
            if (Pressed(ImGuiKey.G)) { _gotoOpen = true; _findFocus = true; _gotoText = ""; _focused = false; return; }
            if (Pressed(ImGuiKey.Space)) { RequestCompletion(); return; }
            if (shift && Pressed(ImGuiKey.K)) { DeleteLines(); return; }
        }
        if (alt && !ctrl)
        {
            if (Pressed(ImGuiKey.UpArrow)) { MoveLines(-1); return; }
            if (Pressed(ImGuiKey.DownArrow)) { MoveLines(1); return; }
        }
        if (Pressed(ImGuiKey.F3) && _findText.Length > 0) { FindNext(!shift); return; }
        if (Pressed(ImGuiKey.Escape))
        {
            if (_findOpen || _gotoOpen) { _findOpen = _gotoOpen = false; return; }
            _anchor = _cursor;
        }

        // ---- Navigation ----
        if (Pressed(ImGuiKey.LeftArrow))
        {
            if (HasSelection && !shift) MoveCursor(Selection.Start, false);
            else MoveCursor(ctrl ? WordLeft(_cursor) : StepLeft(_cursor), shift);
            _preferredCol = -1;
            CloseCompletion();
        }
        if (Pressed(ImGuiKey.RightArrow))
        {
            if (HasSelection && !shift) MoveCursor(Selection.End, false);
            else MoveCursor(ctrl ? WordRight(_cursor) : StepRight(_cursor), shift);
            _preferredCol = -1;
            CloseCompletion();
        }
        if (!_completionOpen && Pressed(ImGuiKey.UpArrow)) MoveVertical(-1, shift);
        if (!_completionOpen && Pressed(ImGuiKey.DownArrow)) MoveVertical(1, shift);
        if (!_completionOpen && Pressed(ImGuiKey.PageUp)) MoveVertical(-pageLines, shift);
        if (!_completionOpen && Pressed(ImGuiKey.PageDown)) MoveVertical(pageLines, shift);
        if (Pressed(ImGuiKey.Home))
        {
            // Smart home: first press goes to the first non-space character.
            if (ctrl) MoveCursor(new TextPos(0, 0), shift);
            else
            {
                int indent = Indentation(_lines[_cursor.Line]);
                MoveCursor(new TextPos(_cursor.Line, _cursor.Col == indent ? 0 : indent), shift);
            }
            _preferredCol = -1;
        }
        if (Pressed(ImGuiKey.End))
        {
            MoveCursor(ctrl ? new TextPos(_lines.Count - 1, _lines[^1].Length) : new TextPos(_cursor.Line, _lines[_cursor.Line].Length), shift);
            _preferredCol = -1;
        }

        // ---- Editing keys ----
        if (Pressed(ImGuiKey.Backspace)) Backspace(ctrl);
        if (Pressed(ImGuiKey.Delete)) DeleteForward(ctrl);
        if (!_completionOpen && (Pressed(ImGuiKey.Enter) || Pressed(ImGuiKey.KeypadEnter))) NewLine();
        if (!_completionOpen && Pressed(ImGuiKey.Tab)) { if (shift) Outdent(); else Indent(); }

        // ---- Typed characters ----
        if (!ctrl || alt)   // AltGr on some keyboards reports Ctrl+Alt
        {
            var queue = io.InputQueueCharacters;
            for (int i = 0; i < queue.Size; i++)
            {
                char c = (char)queue[i];
                if (c >= 32 && c != 127) TypeChar(c);
            }
        }
    }

    private TextPos StepLeft(TextPos p) =>
        p.Col > 0 ? p with { Col = p.Col - 1 } : p.Line > 0 ? new TextPos(p.Line - 1, _lines[p.Line - 1].Length) : p;

    private TextPos StepRight(TextPos p) =>
        p.Col < _lines[p.Line].Length ? p with { Col = p.Col + 1 } : p.Line < _lines.Count - 1 ? new TextPos(p.Line + 1, 0) : p;

    private static int CharClass(char c) => char.IsWhiteSpace(c) ? 0 : char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;

    private TextPos WordLeft(TextPos p)
    {
        if (p.Col == 0) return StepLeft(p);
        string line = _lines[p.Line];
        int i = p.Col;
        while (i > 0 && CharClass(line[i - 1]) == 0) i--;
        if (i == 0) return new TextPos(p.Line, 0);
        int cls = CharClass(line[i - 1]);
        while (i > 0 && CharClass(line[i - 1]) == cls) i--;
        return new TextPos(p.Line, i);
    }

    private TextPos WordRight(TextPos p)
    {
        string line = _lines[p.Line];
        if (p.Col >= line.Length) return StepRight(p);
        int i = p.Col;
        int cls = CharClass(line[i]);
        while (i < line.Length && CharClass(line[i]) == cls) i++;
        while (i < line.Length && CharClass(line[i]) == 0) i++;
        return new TextPos(p.Line, i);
    }

    private void MoveVertical(int delta, bool select)
    {
        if (_preferredCol < 0) _preferredCol = _cursor.Col;
        int line = Math.Clamp(_cursor.Line + delta, 0, _lines.Count - 1);
        MoveCursor(new TextPos(line, Math.Min(_preferredCol, _lines[line].Length)), select);
    }

    private void SelectAll()
    {
        _anchor = new TextPos(0, 0);
        _cursor = new TextPos(_lines.Count - 1, _lines[^1].Length);
    }

    // ---- Clipboard: with nothing selected, copy/cut take the whole line (VS behavior).

    private void Copy()
    {
        if (HasSelection) ImGui.SetClipboardText(GetRange(Selection.Start, Selection.End));
        else ImGui.SetClipboardText(_lines[_cursor.Line] + "\n");
    }

    private void Cut()
    {
        Copy();
        PushUndo("cut");
        if (HasSelection) { var (s, e) = Selection; DeleteRange(s, e); }
        else DeleteLines();
    }

    private void Paste()
    {
        string text = ImGui.GetClipboardText() ?? "";
        if (text.Length == 0) return;
        PushUndo("paste");
        Insert(text);
    }

    // ---- Typing ------------------------------------------------------------------------

    private static readonly Dictionary<char, char> Pairs = new() { ['('] = ')', ['['] = ']', ['{'] = '}', ['"'] = '"', ['\''] = '\'' };

    private void TypeChar(char c)
    {
        PushUndo("type");
        string line = _lines[_cursor.Line];
        char next = _cursor.Col < line.Length ? line[_cursor.Col] : '\0';
        char prev = _cursor.Col > 0 ? line[_cursor.Col - 1] : '\0';

        // Typing a bracket or quote around a selection wraps it.
        if (HasSelection && Pairs.TryGetValue(c, out var wrapClose))
        {
            var (s, e) = Selection;
            string inner = GetRange(s, e);
            DeleteRange(s, e);
            Insert($"{c}{inner}{wrapClose}");
            return;
        }

        // Typing a closer that's already there just steps over it.
        if (!HasSelection && (c == ')' || c == ']' || c == '}' || c == '"' || c == '\'') && next == c)
        {
            MoveCursor(_cursor with { Col = _cursor.Col + 1 }, false);
            CompletionAfterTyping(c);
            return;
        }

        // Typing "}" on a line with only spaces: outdent it to match its "{".
        if (c == '}' && line[.._cursor.Col].Trim().Length == 0 && _cursor.Col >= TabSize)
        {
            _lines[_cursor.Line] = line[TabSize..];
            _cursor = _anchor = _cursor with { Col = _cursor.Col - TabSize };
        }

        // Auto-close brackets and quotes when the next character "allows" it.
        bool nextAllows = next == '\0' || char.IsWhiteSpace(next) || ")]};,".Contains(next);
        if (Pairs.TryGetValue(c, out var close) && nextAllows &&
            !((c == '"' || c == '\'') && (char.IsLetterOrDigit(prev) || prev == '"' || prev == '\'')))
        {
            Insert($"{c}{close}");
            MoveCursor(_cursor with { Col = _cursor.Col - 1 }, false);
        }
        else Insert(c.ToString());

        CompletionAfterTyping(c);
    }

    private void Backspace(bool word)
    {
        PushUndo("delete");
        if (HasSelection) { var (s, e) = Selection; DeleteRange(s, e); return; }
        if (_cursor == new TextPos(0, 0)) return;
        if (word) { DeleteRange(WordLeft(_cursor), _cursor); RefilterCompletion(); return; }

        string line = _lines[_cursor.Line];
        int col = _cursor.Col;
        if (col > 0 && col < line.Length && Pairs.TryGetValue(line[col - 1], out var close) && line[col] == close)
        {
            // Deleting an opener right before its closer removes both: "(|)" → "|".
            DeleteRange(_cursor with { Col = col - 1 }, _cursor with { Col = col + 1 });
        }
        else if (col > 0 && line[..col].Trim().Length == 0)
        {
            // In leading indentation, back up a whole indent level.
            int target = (col - 1) / TabSize * TabSize;
            DeleteRange(_cursor with { Col = target }, _cursor);
        }
        else DeleteRange(StepLeft(_cursor), _cursor);
        RefilterCompletion();
    }

    private void DeleteForward(bool word)
    {
        PushUndo("delete");
        if (HasSelection) { var (s, e) = Selection; DeleteRange(s, e); return; }
        DeleteRange(_cursor, word ? WordRight(_cursor) : StepRight(_cursor));
    }

    // Enter keeps the current indentation, adds a level after "{", and
    // splits "{|}" into three lines with the cursor indented in the middle.
    private void NewLine()
    {
        PushUndo("enter");
        if (HasSelection) { var (s, e) = Selection; DeleteRange(s, e); }
        string line = _lines[_cursor.Line];
        string before = line[.._cursor.Col], after = line[_cursor.Col..];
        string indent = new(' ', Indentation(line));
        bool opens = before.TrimEnd().EndsWith('{') || before.TrimEnd().EndsWith('(') || before.TrimEnd().EndsWith('[');
        string inner = opens ? indent + new string(' ', TabSize) : indent;

        // Leading spaces after the cursor would double up with the new indent.
        int trimmed = after.Length - after.TrimStart(' ').Length;
        if (trimmed > 0) DeleteRange(_cursor, _cursor with { Col = _cursor.Col + trimmed });

        if (opens && after.TrimStart().Length > 0 && "}])".Contains(after.TrimStart()[0]))
        {
            Insert("\n" + inner + "\n" + indent);
            MoveCursor(new TextPos(_cursor.Line - 1, inner.Length), false);
        }
        else Insert("\n" + inner);
    }

    private (int First, int Last) SelectedLines()
    {
        var (s, e) = Selection;
        int last = e.Line > s.Line && e.Col == 0 ? e.Line - 1 : e.Line;
        return (s.Line, last);
    }

    private void Indent()
    {
        PushUndo("indent");
        if (HasSelection && Selection.Start.Line != Selection.End.Line)
        {
            var (first, last) = SelectedLines();
            for (int i = first; i <= last; i++) _lines[i] = new string(' ', TabSize) + _lines[i];
            _cursor = Clamp(_cursor with { Col = _cursor.Col + TabSize });
            _anchor = Clamp(_anchor with { Col = _anchor.Col > 0 ? _anchor.Col + TabSize : 0 });
            Changed();
            return;
        }
        int spaces = TabSize - _cursor.Col % TabSize;
        Insert(new string(' ', spaces));
    }

    private void Outdent()
    {
        PushUndo("indent");
        var (first, last) = SelectedLines();
        for (int i = first; i <= last; i++)
        {
            int remove = Math.Min(TabSize, Indentation(_lines[i]));
            _lines[i] = _lines[i][remove..];
            if (i == _cursor.Line) _cursor = _cursor with { Col = Math.Max(0, _cursor.Col - remove) };
            if (i == _anchor.Line) _anchor = _anchor with { Col = Math.Max(0, _anchor.Col - remove) };
        }
        Changed();
    }

    // Ctrl+/ — comment out the selected lines, or uncomment them if they
    // all already are.
    private void ToggleComment()
    {
        PushUndo("comment");
        var (first, last) = SelectedLines();
        var range = Enumerable.Range(first, last - first + 1).Where(i => _lines[i].Trim().Length > 0).ToList();
        if (range.Count == 0) return;
        bool allCommented = range.All(i => _lines[i].TrimStart().StartsWith("//"));
        int minIndent = range.Min(i => Indentation(_lines[i]));
        foreach (int i in range)
        {
            string l = _lines[i];
            if (allCommented)
            {
                int at = l.IndexOf("//", StringComparison.Ordinal);
                int len = at + 2 < l.Length && l[at + 2] == ' ' ? 3 : 2;
                _lines[i] = l.Remove(at, len);
            }
            else _lines[i] = l.Insert(minIndent, "// ");
        }
        _cursor = Clamp(_cursor);
        _anchor = Clamp(_anchor);
        Changed();
    }

    private void DuplicateLines()
    {
        PushUndo("duplicate");
        var (first, last) = SelectedLines();
        var copy = _lines.GetRange(first, last - first + 1);
        _lines.InsertRange(last + 1, copy);
        int shift = copy.Count;
        _cursor = _cursor with { Line = _cursor.Line + shift };
        _anchor = _anchor with { Line = _anchor.Line + shift };
        Changed();
    }

    private void DeleteLines()
    {
        PushUndo("delete-line");
        var (first, last) = SelectedLines();
        _lines.RemoveRange(first, last - first + 1);
        if (_lines.Count == 0) _lines.Add("");
        _cursor = _anchor = Clamp(new TextPos(first, _cursor.Col));
        Changed();
    }

    // Alt+Up/Down — move the selected lines up or down.
    private void MoveLines(int direction)
    {
        var (first, last) = SelectedLines();
        if (direction < 0 && first == 0 || direction > 0 && last >= _lines.Count - 1) return;
        PushUndo("move-line");
        var block = _lines.GetRange(first, last - first + 1);
        _lines.RemoveRange(first, block.Count);
        _lines.InsertRange(first + direction, block);
        _cursor = _cursor with { Line = _cursor.Line + direction };
        _anchor = _anchor with { Line = _anchor.Line + direction };
        Changed();
    }

    // The bracket at/before the cursor and its partner, if any.
    private List<TextPos> FindMatchingBracket()
    {
        var result = new List<TextPos>();
        if (!_focused) return result;
        const string open = "([{", close = ")]}";
        string line = _lines[_cursor.Line];
        int col = _cursor.Col < line.Length && (open + close).Contains(line[_cursor.Col]) ? _cursor.Col
            : _cursor.Col > 0 && (open + close).Contains(line[_cursor.Col - 1]) ? _cursor.Col - 1 : -1;
        if (col < 0) return result;

        char c = line[col];
        bool forward = open.Contains(c);
        char partner = forward ? close[open.IndexOf(c)] : open[close.IndexOf(c)];
        int depth = 0, scanned = 0;
        var p = new TextPos(_cursor.Line, col);
        while (scanned++ < 20000)
        {
            char ch = _lines[p.Line].Length > p.Col ? _lines[p.Line][p.Col] : '\n';
            if (ch == c) depth++;
            else if (ch == partner && --depth == 0)
            {
                result.Add(new TextPos(_cursor.Line, col));
                result.Add(p);
                return result;
            }
            var next = forward ? StepRight(p) : StepLeft(p);
            if (next == p) break;
            p = next;
        }
        return result;
    }

    // =========================================================================
    //  Autocomplete
    // =========================================================================

    private void CompletionAfterTyping(char c)
    {
        if (c == '.') { RequestCompletion(); return; }
        if (char.IsLetterOrDigit(c) || c == '_')
        {
            if (_completionOpen) RefilterCompletion();
            else if (CurrentWordLength() == 1 && !char.IsDigit(c)) RequestCompletion();   // start of a new word
            return;
        }
        CloseCompletion();
    }

    private int CurrentWordLength()
    {
        string line = _lines[_cursor.Line];
        int i = _cursor.Col;
        while (i > 0 && (char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_')) i--;
        return _cursor.Col - i;
    }

    private void RequestCompletion()
    {
        if (CompletionProvider == null) return;
        // Never inside a comment or string on this line.
        var tokens = _lineTokens.Count > _cursor.Line ? _lineTokens[_cursor.Line] : new List<Token>();
        if (tokens.Any(t => (t.Kind is TokenKind.Comment or TokenKind.String) && _cursor.Col > t.Start && _cursor.Col <= t.Start + t.Length))
            return;
        string text = Text;
        int offset = Offset(_cursor);
        var provider = CompletionProvider;
        _completionTask = Task.Run(() => provider(text, offset));
    }

    private void PollCompletion()
    {
        if (_completionTask is not { IsCompleted: true } task) return;
        _completionTask = null;
        if (task.IsFaulted) return;
        var (items, replaceStart) = task.Result;
        _completionAll = items;
        _completionStart = FromOffset(replaceStart);
        if (_completionStart.Line != _cursor.Line || _completionStart.Col > _cursor.Col) { CloseCompletion(); return; }
        _completionOpen = true;
        RefilterCompletion();
    }

    private void RefilterCompletion()
    {
        if (!_completionOpen) return;
        if (_cursor.Line != _completionStart.Line || _cursor.Col < _completionStart.Col) { CloseCompletion(); return; }
        string prefix = _lines[_cursor.Line][_completionStart.Col.._cursor.Col];

        // Rank: exact-case prefix, any-case prefix, then "contains"; within
        // that, your own members before types/keywords, then shorter names.
        int Score(CompletionItem item)
        {
            if (prefix.Length == 0) return 0;
            if (item.Name.StartsWith(prefix, StringComparison.Ordinal)) return 0;
            if (item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 1;
            if (item.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)) return 2;
            return 99;
        }
        int KindRank(CompletionItem item) => item.Kind switch
        {
            CompletionKind.Local or CompletionKind.Field or CompletionKind.Property => 0,
            CompletionKind.Method or CompletionKind.EnumMember or CompletionKind.Event => 1,
            CompletionKind.Keyword => 2,
            _ => 3,
        };

        _completionShown = _completionAll
            .Select(i => (Item: i, Score: Score(i)))
            .Where(x => x.Score < 99)
            .OrderBy(x => x.Score).ThenBy(x => prefix.Length == 0 ? 0 : KindRank(x.Item)).ThenBy(x => x.Item.Name.Length).ThenBy(x => x.Item.Name)
            .Take(200)
            .Select(x => x.Item)
            .ToList();

        // An exact, complete match with nothing else to offer: just close.
        if (_completionShown.Count == 0 || (_completionShown.Count == 1 && _completionShown[0].Name == prefix)) { CloseCompletion(); return; }
        _completionIndex = 0;
    }

    private void CloseCompletion()
    {
        _completionOpen = false;
        _completionTask = null;
    }

    private void AcceptCompletion()
    {
        if (_completionIndex < 0 || _completionIndex >= _completionShown.Count) { CloseCompletion(); return; }
        var item = _completionShown[_completionIndex];
        PushUndo("complete");
        DeleteRange(_completionStart, _cursor);
        string line = _lines[_cursor.Line];
        bool nextIsParen = _cursor.Col < line.Length && line[_cursor.Col] == '(';
        if (item.Kind == CompletionKind.Method && !nextIsParen)
        {
            // Methods get their parentheses; the cursor lands inside them
            // unless the method takes no arguments.
            Insert(item.Name + "()");
            if (!item.Detail.Contains("()")) MoveCursor(_cursor with { Col = _cursor.Col - 1 }, false);
        }
        else Insert(item.Name);
        CloseCompletion();
    }

    private static (string Letter, uint Color) KindBadge(CompletionKind kind) => kind switch
    {
        CompletionKind.Method => ("M", Col(0xF0F0F0)),
        CompletionKind.Property => ("P", Col(0xD0D0D0)),
        CompletionKind.Field => ("F", Col(0xC4C4C4)),
        CompletionKind.Event => ("E", Col(0xB0B0B0)),
        CompletionKind.Local => ("L", Col(0x9A9A9A)),
        CompletionKind.EnumMember => ("V", Col(0xB8B8B8)),
        CompletionKind.Type => ("T", Col(0xE6E6E6)),
        CompletionKind.Namespace => ("N", Col(0x7A7A7A)),
        _ => ("K", Col(0xFFFFFF)),
    };

    private void DrawCompletionPopup(Vector2 position)
    {
        const int visibleRows = 10;
        float rowHeight = ImGui.GetTextLineHeight() + 4;
        float width = 380;
        int rows = Math.Min(visibleRows, _completionShown.Count);
        var detail = _completionShown.Count > 0 ? _completionShown[Math.Clamp(_completionIndex, 0, _completionShown.Count - 1)].Detail : "";

        ImGui.SetNextWindowPos(position + new Vector2(-28, 2));
        ImGui.SetNextWindowSize(new Vector2(width, rows * rowHeight + ImGui.GetTextLineHeightWithSpacing() + 18));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.07f, 0.07f, 0.07f, 0.99f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, 0.35f));
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("##completion" + GetHashCode(), flags))
        {
            _popupMin = ImGui.GetWindowPos();
            _popupMax = _popupMin + ImGui.GetWindowSize();
            ImGui.BeginChild("list", new Vector2(0, rows * rowHeight));
            for (int i = 0; i < _completionShown.Count; i++)
            {
                var item = _completionShown[i];
                var (letter, color) = KindBadge(item.Kind);
                var p = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable($"##c{i}", i == _completionIndex, ImGuiSelectableFlags.None, new Vector2(0, rowHeight - 4)))
                {
                    _completionIndex = i;
                    AcceptCompletion();
                    break;
                }
                if (i == _completionIndex && ImGui.IsWindowAppearing() == false) ImGui.SetScrollHereY();
                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(p + new Vector2(2, 1), p + new Vector2(16, rowHeight - 3), color);
                dl.AddText(p + new Vector2(5, 0), Col(0x0B0B0B), letter);
                dl.AddText(p + new Vector2(22, 0), Col(0xF2F2F2), item.Name);
            }
            ImGui.EndChild();
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1));
            ImGui.TextUnformatted(detail.Length > 60 ? detail[..57] + "..." : detail);
            ImGui.PopStyleColor();
        }
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    // =========================================================================
    //  Find / replace / go to line
    // =========================================================================

    private void OpenFind(bool replace)
    {
        _findOpen = true;
        _replaceOpen |= replace;
        _focused = false;   // typing now goes to the find box, not the code
        _gotoOpen = false;
        _findFocus = true;
        // Seed the search with the selection (if it's on one line) or the word at the cursor.
        if (HasSelection && Selection.Start.Line == Selection.End.Line) _findText = GetRange(Selection.Start, Selection.End);
        else if (_findText.Length == 0)
        {
            var (s, e) = WordAt(_cursor);
            if (e.Col > s.Col) _findText = GetRange(s, e);
        }
    }

    private void UpdateFindMatches()
    {
        string query = _findText + (_matchCase ? "|cs" : "|ci");
        if (_findMatchesVersion == Version && _findMatchesQuery == query) return;
        _findMatchesVersion = Version;
        _findMatchesQuery = query;
        _findMatches.Clear();
        if (_findText.Length == 0) return;
        var comparison = _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int i = 0; i < _lines.Count; i++)
        {
            int at = 0;
            while ((at = _lines[i].IndexOf(_findText, at, comparison)) >= 0)
            {
                _findMatches.Add(new TextPos(i, at));
                at += Math.Max(1, _findText.Length);
                if (_findMatches.Count > 5000) return;
            }
        }
    }

    private void FindNext(bool forward)
    {
        UpdateFindMatches();
        if (_findMatches.Count == 0) return;
        var from = forward ? Selection.End : Selection.Start;
        var match = forward
            ? _findMatches.FirstOrDefault(m => m >= from, _findMatches[0])
            : _findMatches.LastOrDefault(m => m < from, _findMatches[^1]);
        _anchor = match;
        _cursor = match with { Col = match.Col + _findText.Length };
        _centerOnLine = match.Line;
    }

    private bool SelectionIsMatch()
    {
        if (!HasSelection || Selection.Start.Line != Selection.End.Line) return false;
        string selected = GetRange(Selection.Start, Selection.End);
        return string.Equals(selected, _findText, _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private void ReplaceOne()
    {
        if (!SelectionIsMatch()) { FindNext(true); return; }
        PushUndo("replace");
        Insert(_replaceText);
        FindNext(true);
    }

    private void ReplaceAll()
    {
        if (_findText.Length == 0) return;
        PushUndo("replace");
        var comparison = _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int count = 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            int before = _lines[i].Length;
            string replaced = _lines[i].Replace(_findText, _replaceText, comparison);
            if (replaced != _lines[i]) { count++; _lines[i] = replaced; }
        }
        _cursor = _anchor = Clamp(_cursor);
        if (count > 0) Changed();
    }

    private void DrawFindBar(ImFontPtr font)
    {
        var start = ImGui.GetCursorScreenPos();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 3));
        if (_gotoOpen)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("Go to line");
            ImGui.SameLine();
            if (_findFocus) { ImGui.SetKeyboardFocusHere(); _findFocus = false; }
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputText("##goto", ref _gotoText, 10, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsDecimal))
            {
                if (int.TryParse(_gotoText, out int line)) GoToLine(line);
                _gotoOpen = false;
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"(1 - {_lines.Count})");
            ImGui.SameLine();
            if (ImGui.SmallButton("x##closegoto") || ImGui.IsKeyPressed(ImGuiKey.Escape)) { _gotoOpen = false; _focused = true; }
        }
        else
        {
            UpdateFindMatches();
            if (_findFocus) { ImGui.SetKeyboardFocusHere(); _findFocus = false; }
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputTextWithHint("##find", "Find", ref _findText, 256, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                FindNext(!ImGui.GetIO().KeyShift);
                _findFocus = true;   // keep typing / pressing Enter in the box
            }
            ImGui.SameLine();
            int current = _findMatches.FindIndex(m => m == Selection.Start) + 1;
            ImGui.TextDisabled(_findText.Length == 0 ? "" : _findMatches.Count == 0 ? "no results" : $"{Math.Max(current, 0)} of {_findMatches.Count}");
            ImGui.SameLine();
            if (ImGui.SmallButton("<")) FindNext(false);
            ImGui.SameLine();
            if (ImGui.SmallButton(">")) FindNext(true);
            ImGui.SameLine();
            ImGui.Checkbox("Aa", ref _matchCase);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Match case");
            ImGui.SameLine();
            if (ImGui.SmallButton(_replaceOpen ? "Hide replace" : "Replace...")) _replaceOpen = !_replaceOpen;
            ImGui.SameLine();
            if (ImGui.SmallButton("x##closefind") || (ImGui.IsKeyPressed(ImGuiKey.Escape) && !_focused)) { _findOpen = false; _focused = true; }

            if (_replaceOpen)
            {
                ImGui.SetNextItemWidth(220);
                ImGui.InputTextWithHint("##replace", "Replace with", ref _replaceText, 256);
                ImGui.SameLine();
                if (ImGui.SmallButton("Replace")) ReplaceOne();
                ImGui.SameLine();
                if (ImGui.SmallButton("Replace All")) ReplaceAll();
            }
        }
        ImGui.PopStyleVar();
        _findBarMin = start;
        _findBarMax = ImGui.GetCursorScreenPos();
    }
}
