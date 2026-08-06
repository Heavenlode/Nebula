# Nebula bots

Scripted clients. A bot is a **real** Nebula client — full spawn stream, prediction,
reconciliation — launched as its own process. The only thing that differs from a human player is
where its input comes from.

Use them to populate a world for play testing, to reproduce a desync with more than two
participants, or as the basis for load scenarios.

## Writing a behavior

Subclass `BotBehavior` anywhere in your game project. It is discovered by reflection, so no
registration step is needed.

```csharp
using Nebula;
using Nebula.Bots;

public partial class MyBot : BotBehavior
{
    public override Task BotStartup()
    {
        // Runs before the client connects, and is awaited. Establish a session here.
        MyGame.Session.Token = $"bot-{BotId}";
        return Task.CompletedTask;
    }

    public override void BotReady()
    {
        // First tick on which OwnedNodes is non-empty — the bot now has a body.
    }

    public override void NetworkProcess(Tick tick, double delta)
    {
        // Once per network tick, immediately before prediction.
        Input.ActionPress("move_forward");
    }
}
```

### Act through input actions

Prefer `Input.ActionPress` / `Input.ActionRelease` over touching the network layer. Your game's own
input-sampling code then reads those actions exactly as it reads a keyboard, so prediction, input
send, and reconciliation all follow the path a real player takes — there is no parallel "bot input"
route that can drift from the real one. This works under `--headless`.

`NetworkProcess` runs *before* the prediction pass for that tick, which is where a human's input
has just been sampled, so an action pressed there lands on the same tick it would have for a player.

Behaviors that need to do more than move can reach their own nodes through `OwnedNodes` — to call a
NetFunction, for instance.

### Two things worth knowing

- **`OwnedNodes` is empty until the server spawns your character.** Guard on it, or do your setup in
  `BotReady`.
- **Release what you press.** A latched movement key survives a state change, and a bot that
  switches control schemes mid-leg with thrust still held will accelerate forever. Release on
  transitions and in `_ExitTree`.

## Running them

From the editor: **Nebula → Manage Configurations…**, set a bot count and pick a behavior, then use
the Play button. Bots launch alongside the configuration's real clients and join the same world.

By hand:

```
godot --path <project> --headless --bot --botId=0 --botBehavior=MyBot
```

| Argument | Meaning |
| --- | --- |
| `--bot` | Marks the process as a bot. Without it none of this runtime loads. |
| `--botId=N` | Zero-based index. Stable for the process, so identity can be derived from it. |
| `--botBehavior=Name` | `BotBehavior` subclass, by short or full type name. |

The behavior is resolved by **type name rather than script path**: a C# script resource cannot be
instantiated through the Godot script API the way a GDScript one can, and reflection is also what
lets the editor offer a dropdown of the behaviors that actually exist.

## Server metrics

Bots are most useful with numbers attached. Launch the server with `--metrics` (optionally
`--metricsInterval=<seconds>`, default 1) and it writes one JSON line per world per interval,
prefixed `NEBULA_METRICS`:

```
NEBULA_METRICS {"world":"…","tick":1830,"peers":11,"tick_ms":{"p50":1.2,"p95":3.4,…},…}
```

Tick timing percentiles, bytes out per peer per second, GC collection deltas, RTT spread, and
MTU-exceeded / ack-timeout counters. Recording is allocation-free so that measuring does not
perturb what is measured. This goes to stdout rather than the debug channel deliberately —
`DebugHub` only produces frames while a debugger is attached and drops lossy frames when its queue
backs up, which would lose exactly the samples a loaded run exists to capture.
