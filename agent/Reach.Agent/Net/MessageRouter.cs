using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Reach.Agent.Input;
using Reach.Agent.Storage;
using Reach.Protocol;

namespace Reach.Agent.Net;

/// <summary>
/// Acts on one session's decoded messages (spec §4). Never logs message contents: no
/// keystrokes, text or terminal data (spec §3.7).
/// </summary>
public sealed class MessageRouter(Device device, AgentContext agent, Action<Message> send, ILogger log)
{
    public void Handle(Message message)
    {
        try
        {
            switch (message)
            {
                case Hello hello:
                    agent.Devices.Seen(device.Id, hello.DeviceName);
                    send(new Welcome(agent.PcName, agent.AgentVersion));
                    agent.Sessions.ReportConnected(agent.Devices.All.FirstOrDefault(d => d.Id == device.Id)?.Name ?? DeviceStore.DefaultName);
                    break;
                case Ping:
                    send(new Pong());
                    break;
                case Pong:
                    break;
                case MouseMove m:
                    agent.Input.Move(m.Dx, m.Dy);
                    break;
                case MouseButton b:
                    agent.Input.Button(b.Button, b.Down);
                    break;
                case MouseScroll s:
                    agent.Input.Scroll(s.Dx, s.Dy);
                    break;
                case KeyText t:
                    agent.Input.Text(t.Text);
                    break;
                case KeyCombo c:
                    agent.Input.Combo(c.Keys);
                    break;
                case TermOpen o:
                    agent.Terminal.Open(o.Cols, o.Rows, send);
                    break;
                case TermInput i:
                    agent.Terminal.Input(i.Data);
                    break;
                case TermResize r:
                    agent.Terminal.Resize(r.Cols, r.Rows);
                    break;
                case CmdList:
                    send(new CmdListed([.. agent.Commands.List()]));
                    break;
                case CmdRun run:
                    _ = RunCommandAsync(run.Id);
                    break;
                default:
                    send(new ErrorMessage("unknown_message", $"the PC does not accept {message.GetType().Name} messages"));
                    break;
            }
        }
        catch (BadArgsException e)
        {
            send(new ErrorMessage("bad_args", e.Message));
        }
        catch (Win32Exception e)
        {
            log.LogError(e, "Failed to handle {Type}", message.GetType().Name);
            send(new ErrorMessage("failed", e.Message));
        }
    }

    /// <summary>Lets go of held keys and buttons and detaches from the terminal (spec §5.1, §5.2).</summary>
    public void SessionEnded()
    {
        agent.Input.ReleaseAll();
        agent.Terminal.Detach(send);
    }

    // Not tied to the session: "sleep" drops the connection while its script is still finishing.
    private async Task RunCommandAsync(string id)
    {
        try
        {
            send(await agent.Commands.RunAsync(id, CancellationToken.None));
        }
        catch (Exception e)
        {
            log.LogError(e, "Saved command {Id} failed", id);
            send(new CmdResult(id, false, -1, "failed to run"));
        }
    }
}
