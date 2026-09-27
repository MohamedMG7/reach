namespace Reach.Agent.Input;

/// <summary>A message field the PC can't act on; the session replies with an error "bad_args".</summary>
public sealed class BadArgsException(string message) : Exception(message);

/// <summary>
/// Turns input messages into sink calls (spec §5.1) and remembers what is held down, so
/// <see cref="ReleaseAll"/> can let go of everything when a session ends.
/// </summary>
public sealed class InputService(IInputSink sink)
{
    public const int MaxComboKeys = 6;

    private readonly Lock _lock = new();
    private readonly HashSet<MouseButtonKind> _buttons = [];
    private readonly List<ushort> _keys = [];

    public void Move(int dx, int dy)
    {
        lock (_lock) sink.MoveBy(dx, dy);
    }

    public void Button(string name, bool down)
    {
        var button = name switch
        {
            "left" => MouseButtonKind.Left,
            "right" => MouseButtonKind.Right,
            "middle" => MouseButtonKind.Middle,
            _ => throw new BadArgsException($"unknown mouse button: {name}"),
        };
        lock (_lock)
        {
            if (down) _buttons.Add(button);
            else _buttons.Remove(button);
            sink.Button(button, down);
        }
    }

    public void Scroll(int dx, int dy)
    {
        lock (_lock) sink.Scroll(dx, dy);
    }

    /// <summary>Types text as Unicode; "\n" and "\b" become the Enter and Backspace keys.</summary>
    public void Text(string text)
    {
        lock (_lock)
        {
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\n': Tap(KeyMap.Enter); break;
                    case '\b': Tap(KeyMap.Backspace); break;
                    default: sink.Unicode(c); break;
                }
            }
        }
    }

    /// <summary>Presses the keys in order, then releases them in reverse. Validates every name first.</summary>
    public void Combo(IReadOnlyList<string> names)
    {
        if (names.Count is 0 or > MaxComboKeys) throw new BadArgsException($"key.combo needs 1 to {MaxComboKeys} keys");
        var vks = names.Select(n => KeyMap.TryGet(n, out var vk) ? vk : throw new BadArgsException($"unknown key: {n}")).ToList();
        lock (_lock)
        {
            foreach (var vk in vks) Press(vk);
            for (var i = vks.Count - 1; i >= 0; i--) Release(vks[i]);
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            for (var i = _keys.Count - 1; i >= 0; i--) sink.Key(_keys[i], down: false);
            _keys.Clear();
            foreach (var button in _buttons) sink.Button(button, down: false);
            _buttons.Clear();
        }
    }

    private void Tap(ushort vk)
    {
        Press(vk);
        Release(vk);
    }

    private void Press(ushort vk)
    {
        _keys.Add(vk);
        sink.Key(vk, down: true);
    }

    private void Release(ushort vk)
    {
        _keys.RemoveAt(_keys.LastIndexOf(vk));
        sink.Key(vk, down: false);
    }
}
