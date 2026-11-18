using System.Text;

namespace Amethyst;

/// <summary>
/// Pure, terminal-independent single-line editor used by ServerConsole.
/// Its state can be regression tested without an interactive TTY.
/// </summary>
public sealed class ConsoleLineEditor
{
    public const int MaxCommandLength = 2048;
    private readonly StringBuilder _line = new();
    private readonly List<string> _history = new();
    private readonly int _historyLimit;
    private int _cursor;
    private int _historyPosition = -1;
    private string _draft = "";
    private IReadOnlyList<string>? _tabCandidates;
    private int _tabPosition;

    public ConsoleLineEditor(int historyLimit = 100)
    {
        if (historyLimit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(historyLimit));
        _historyLimit = historyLimit;
    }

    public string Text => _line.ToString();
    public int Cursor => _cursor;
    public IReadOnlyList<string> History => _history.AsReadOnly();

    public ConsoleEditResult Handle(ConsoleKeyInfo key,
        Func<string, IReadOnlyList<string>> complete)
    {
        if (key.Key != ConsoleKey.Tab) _tabCandidates = null;
        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            switch (key.Key)
            {
                case ConsoleKey.A: _cursor = 0; return new(true, null);
                case ConsoleKey.E: _cursor = _line.Length; return new(true, null);
                case ConsoleKey.U:
                    _line.Remove(0, _cursor);
                    _cursor = 0;
                    _historyPosition = -1;
                    return new(true, null);
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
            {
                string command = _line.ToString().Trim();
                if (command.Length > 0 && (_history.Count == 0 || _history[^1] != command))
                {
                    _history.Add(command);
                    if (_history.Count > _historyLimit) _history.RemoveAt(0);
                }
                ResetLine();
                return new(true, command.Length == 0 ? null : command);
            }
            case ConsoleKey.Backspace:
                if (_cursor > 0)
                {
                    _line.Remove(--_cursor, 1);
                    _historyPosition = -1;
                    return new(true, null);
                }
                return new(false, null);
            case ConsoleKey.Delete:
                if (_cursor < _line.Length)
                {
                    _line.Remove(_cursor, 1);
                    _historyPosition = -1;
                    return new(true, null);
                }
                return new(false, null);
            case ConsoleKey.LeftArrow:
                if (_cursor > 0) --_cursor;
                return new(true, null);
            case ConsoleKey.RightArrow:
                if (_cursor < _line.Length) ++_cursor;
                return new(true, null);
            case ConsoleKey.Home: _cursor = 0; return new(true, null);
            case ConsoleKey.End: _cursor = _line.Length; return new(true, null);
            case ConsoleKey.UpArrow:
                if (_history.Count == 0) return new(false, null);
                if (_historyPosition == -1)
                {
                    _draft = Text;
                    _historyPosition = _history.Count - 1;
                }
                else if (_historyPosition > 0) --_historyPosition;
                SetLine(_history[_historyPosition]);
                return new(true, null);
            case ConsoleKey.DownArrow:
                if (_historyPosition == -1) return new(false, null);
                if (++_historyPosition >= _history.Count)
                {
                    _historyPosition = -1;
                    SetLine(_draft);
                }
                else SetLine(_history[_historyPosition]);
                return new(true, null);
            case ConsoleKey.Tab:
                return Complete(complete);
            default:
                if (key.KeyChar == '\0' || char.IsControl(key.KeyChar) ||
                    (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0 ||
                    _line.Length >= MaxCommandLength)
                    return new(false, null);
                _line.Insert(_cursor++, key.KeyChar);
                _historyPosition = -1;
                return new(true, null);
        }
    }

    private ConsoleEditResult Complete(Func<string, IReadOnlyList<string>> complete)
    {
        if (_tabCandidates is { Count: > 1 })
        {
            _tabPosition = (_tabPosition + 1) % _tabCandidates.Count;
            SetLine(_tabCandidates[_tabPosition]);
            return new(true, null);
        }
        IReadOnlyList<string> candidates = complete(Text[.._cursor]);
        if (candidates.Count == 0) return new(false, null);
        _tabCandidates = candidates;
        _tabPosition = 0;
        if (candidates.Count == 1)
        {
            SetLine(candidates[0]);
            return new(true, null);
        }

        string common = candidates[0];
        foreach (string item in candidates.Skip(1))
        {
            int i = 0;
            while (i < common.Length && i < item.Length &&
                char.ToUpperInvariant(common[i]) == char.ToUpperInvariant(item[i])) ++i;
            common = common[..i];
        }
        if (common.Length > _cursor)
            SetLine(common);
        else
            SetLine(candidates[0]);
        return new(true, null);
    }

    private void SetLine(string value)
    {
        _line.Clear();
        _line.Append(value.AsSpan(0, Math.Min(value.Length, MaxCommandLength)));
        _cursor = _line.Length;
    }

    private void ResetLine()
    {
        _line.Clear();
        _cursor = 0;
        _historyPosition = -1;
        _draft = "";
        _tabCandidates = null;
    }
}

public readonly record struct ConsoleEditResult(bool Redraw, string? Submitted);
