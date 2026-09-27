using System.Text;

namespace Reach.Agent.Terminal;

/// <summary>The most recent terminal output, capped at <paramref name="capacity"/> characters (spec §5.2).</summary>
public sealed class RingText(int capacity)
{
    private readonly StringBuilder _text = new();
    private bool _trimmed;

    public void Append(string text)
    {
        _text.Append(text);
        if (_text.Length <= capacity) return;
        _text.Remove(0, _text.Length - capacity);
        _trimmed = true;
    }

    /// <summary>
    /// What to send a re-attaching phone. Once old output has been dropped, the buffer starts
    /// mid-line (maybe mid-escape-sequence), so replay starts after the first newline instead.
    /// </summary>
    public string Replay()
    {
        var text = _text.ToString();
        if (!_trimmed) return text;
        var newline = text.IndexOf('\n');
        return newline < 0 ? "" : text[(newline + 1)..];
    }

    public void Clear()
    {
        _text.Clear();
        _trimmed = false;
    }
}
