namespace Reach.Agent.Input;

public enum MouseButtonKind { Left, Right, Middle }

/// <summary>The OS input primitives. Production: <see cref="Win32InputSink"/>; tests: a recording fake.</summary>
public interface IInputSink
{
    /// <summary>Moves the cursor by a relative amount, with no OS acceleration.</summary>
    void MoveBy(int dx, int dy);

    void Button(MouseButtonKind button, bool down);

    /// <summary>Raw wheel units (120 = one notch). Positive dy scrolls up, positive dx scrolls right.</summary>
    void Scroll(int dx, int dy);

    void Key(ushort vk, bool down);

    /// <summary>Presses and releases one UTF-16 code unit.</summary>
    void Unicode(char c);
}
