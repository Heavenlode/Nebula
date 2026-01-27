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

Easy enough! Now let's start spawning "Growth Pellets"--collecting these makes the player character grow in size.

The easiest way to do this is to simply create a new Pellet scene and spawn a whole lot of them.