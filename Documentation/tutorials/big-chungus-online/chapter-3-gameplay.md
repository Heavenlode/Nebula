# Big Chungus Online - Gameplay

This chapter covers:
* Processing user inputs
* Keeping score
* Handling property change events

## Handling User Inputs

For this game, the user is simply able to move up, down, left, right, or diagonally. Go ahead and open up the `PlayerController.cs` file for the following changes.

First, we'll define an "input struct" that the client can end to the server. You can put this at the top of the file:

```cs
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayerInput
{
    public bool Up;
    public bool Down;
    public bool Right;
    public bool Left;
}
```

Then we'll prepare the node to handle these inputs:

```cs
    public override void _WorldReady()
    {
        base._WorldReady();
        Network.InitializeInput<PlayerInput>();
    }
```

We'll record those inputs on the client side:
```cs
    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);

        Network.SetInput(new PlayerInput {
            Up = Input.IsActionPressed("ui_up"),
            Down = Input.IsActionPressed("ui_down"),
            Right = Input.IsActionPressed("ui_right"),
            Left = Input.IsActionPressed("ui_left")
        });
    }
```


And we'll consume those inputs, both on client and server:
```cs
    public override void _NetworkProcess(int tick)
    {
        base._NetworkProcess(tick);
        ref readonly var input = ref Network.GetInput<PlayerInput>();
        if (input.Right || input.Left || input.Up || input.Down)
        {
            Direction = new Vector3(input.Up ? -1 : input.Down ? 1 : 0, 0, input.Left ? 1 : input.Right ? -1 : 0).Normalized();
        }

        Position += Direction * 0.10f;
    }
```

So our player character is continuously moving in their most recent direction, and the player can change direction using the arrow keys.

Notice that we're mutating the "Direction" property, and we need it to be synchronized across clients. In order to do that, we need to make it a `NetProperty`.

```cs
    [NetProperty]
    public Vector3 Direction { get; set; } = new Vector3(1, 0, 0);
```

All-in-all, the final `PlayerController.cs` file will look like this:

```cs
using Godot;
using Nebula;
using Nebula.Utility.Tools;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayerInput
{
    public bool Up;
    public bool Down;
    public bool Right;
    public bool Left;
}

public partial class PlayerController : NetNode3D
{
    [NetProperty]
    public Vector3 Direction { get; set; } = new Vector3(1, 0, 0);

    public override void _WorldReady()
    {
        base._WorldReady();
        Network.InitializeInput<PlayerInput>();
    }

    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);

        Network.SetInput(new PlayerInput {
            Up = Input.IsActionPressed("ui_up"),
            Down = Input.IsActionPressed("ui_down"),
            Right = Input.IsActionPressed("ui_right"),
            Left = Input.IsActionPressed("ui_left")
        });
    }

    public override void _NetworkProcess(int tick)
    {
        base._NetworkProcess(tick);
        ref readonly var input = ref Network.GetInput<PlayerInput>();
        if (input.Right || input.Left || input.Up || input.Down)
        {
            Direction = new Vector3(input.Up ? -1 : input.Down ? 1 : 0, 0, input.Left ? 1 : input.Right ? -1 : 0).Normalized();
        }

        Position += Direction * 0.10f;
    }
}
```

That's it! You can run the game again and control your characters using the arrow keys.

![Create Solution](~/images/big-chungus/chapter-3/player-input.gif)

## Collecting Growth Pellets

Now let's start spawning "Growth Pellets"--collecting these makes the player character grow in size.

The easiest way to do this is to simply create a new Pellet scene and spawn a whole lot of them.

For the pellet, we can set it up however we like. For example:

![Create Solution](~/images/big-chungus/chapter-3/pellet.png)

Just a simple Node3D with a Sprite3D and an Area3D (to detect when the player touches it) should do fine. Then just attach a blank NetNode3D script to it.

```cs
using Nebula;

public partial class Pellet : NetNode3D
{
}
```

On the Area3D, we can attach another script to make it easy to consume the pellet.

```cs
using Godot;
using Nebula;

public enum ConsumableAreaType {
    Pellet,
    Player,
}
public partial class ConsumableArea : Area3D
{
    [Export] Node Parent;

    [Export(PropertyHint.Enum, nameof(ConsumableAreaType))]
    public ConsumableAreaType Type;

    public void Consume() {
        (Parent as INetNodeBase).Network.Despawn();
    }
}
```

(Note in the screenshot above it is wired up via the Area3D signals)

> [!NOTE] By attaching the "Parent" node directly to the ConsumbleArea, we can avoid looking it up via `GetParent()`. Calling `GetParent()` would be a memory allocation for the server. If the server does too many interactions with Godot in a short period of time, it can trigger a C# garbage collection cycle, causing the server to have a brief lag spike. For this reason, generally speaking, the server should avoid interacting with Godot wherever possible.

Now we'll update our player scene to also have an Area3D with the same `ConsumableArea` script above. We'll use this Area3D the logic of gaining points or consuming smaller players to become the big chungus.

![Create Solution](~/images/big-chungus/chapter-3/player-area3d.png)

Back to our `Player.cs` script, we'll add a new property to track the score:

```cs
    [NetProperty]
    public int Score { get; set; } = 0;
```

And we'll wire up the Area3D signal to handle consuming pellets. This is only done on the server, since the server handles the spawning and despawning of nodes.

```cs
    public void _OnCollision(Area3D area) {

        if (NetRunner.Instance.IsClient) {
            return;
        }

        if (area is ConsumableArea pellet && pellet.Type == ConsumableAreaType.Pellet) {
            Score++;
            pellet.Consume();
        }
    }
```

This will already work now. If you'd like, you can scatter some pellets around the `game_arena.tscn` and collect them to test it out.

![Create Solution](~/images/big-chungus/chapter-3/collect-pellets.gif)

## Random Pellets And Growth

With this working, we want to go ahead and make pellets start appearing randomly throughout the map. This can be easily done by creating a new NetNode in the `game_arena.tscn`

