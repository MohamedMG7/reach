namespace Reach.Agent.Net;

/// <summary>
/// At most one active session. While a phone is connected, other phones are refused; the same
/// phone reconnecting replaces its old session (the PC may not have noticed that one died yet).
/// </summary>
public sealed class SessionManager
{
    private readonly Lock _lock = new();
    private Session? _active;

    public event Action<Session>? Started;
    public event Action<Session>? Ended;

    /// <summary>Raised with the device name once the phone has said hello.</summary>
    public event Action<string>? DeviceConnected;

    public Session? Active
    {
        get { lock (_lock) return _active; }
    }

    /// <summary>Makes <paramref name="session"/> the active one, unless another phone is connected (returned in <paramref name="current"/>).</summary>
    public bool TryActivate(Session session, out Session? current)
    {
        Session? previous;
        lock (_lock)
        {
            if (_active is not null && _active.Device.Id != session.Device.Id)
            {
                current = _active;
                return false;
            }
            previous = _active;
            _active = session;
        }
        current = null;
        previous?.Close();
        Started?.Invoke(session);
        return true;
    }

    public void Deactivate(Session session)
    {
        lock (_lock)
        {
            if (_active != session) return;
            _active = null;
        }
        Ended?.Invoke(session);
    }

    /// <summary>Closes the active session if it belongs to <paramref name="deviceId"/> (revocation, spec §3.4 step 6).</summary>
    public void CloseDevice(string deviceId)
    {
        var active = Active;
        if (active?.Device.Id == deviceId) active.Close();
    }

    /// <summary>Disconnects the connected phone (the tray's Disconnect), telling it not to reconnect by itself.</summary>
    public void DisconnectActive() => Active?.Disconnect();

    public void ReportConnected(string deviceName) => DeviceConnected?.Invoke(deviceName);
}
