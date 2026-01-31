# Big Chungus Online - Spawning Pellets

This chapter covers:
* Processing user inputs
* Handling property change events
* Networking properties

## Handling User Inputs

For this game, the user is simply able to move up, down, left, right, or diagonally. Go ahead and open up the `PlayerController.cs` file for the following changes.

First, we'll define an "input struct" that the client can send to the server. You can put this at the top of the file:

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
        Position = new Vector3(Mathf.Clamp(Position.X, -100, 100), Position.Y, Mathf.Clamp(Position.Z, -100, 100));
    }
```

So our player character is continuously moving in their most recent direction, and the player can change direction using the arrow keys. We also make sure they can't go too far, by giving their position minimum and maximum values.

Notice that we're mutating the "Direction" property, and we need it to be synchronized across clients. In order to do that, we need to make it a `NetProperty`.

```cs
    [NetProperty]
    public Vector3 Direction { get; set; } = new Vector3(1, 0, 0);
```

`NetProperty` is Nebula's way of synchronizing properties and values across the network. Clients get the values for these variables from the server.

Clients are allowed to try to set these directly (and should for "prediction"! In order to make the game feel instant) Eventually though it will be overwritten by the server.

All-in-all, the final `PlayerController.cs` file will look like this:

```cs
using Godot;
using Nebula;
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
        Position = new Vector3(Mathf.Clamp(Position.X, -100, 100), Position.Y, Mathf.Clamp(Position.Z, -100, 100));
    }
}
```

Notice this demonstrates something very fundamental about how Nebula works:

1. The server is the final authority of NetProperty values
2. Clients can only send inputs to the server
3. The server decides what to do with the inputs
4. The server synchronizes that result back to the clients.

That's it! We can run the game again and control our characters using the arrow keys.

![Create Solution](~/images/big-chungus/chapter-3/movement.gif)

## Collecting Growth Pellets

Now let's start spawning "Growth Pellets"--collecting these makes the player character grow in size.

Our initial instinct might be to create a Pellet scene and spawn a whole bunch of them. However, that's actually not the right way to do this, for a few reasons:

1. Clients can be aware of up to 512 NetScenes at a time. We want thousands of Pellets.
2. Networking variables across multiple net scenes is less efficient than networking multiple variables within the same net scene.
3. In Godot, games are less efficient the more nodes they have.

We'll see how we can actually pack quite a lot of objects and data into an online game, while keeping it running fast and efficiently!

First, let's start by creating a new node called "PelletSpawner", with a MultiMeshInstance3D that defines how we want our pellets to look.

![Create Solution](~/images/big-chungus/chapter-3/pellet-mesh-1.png)

![Create Solution](~/images/big-chungus/chapter-3/pellet-mesh-2.png)

![Create Solution](~/images/big-chungus/chapter-3/pellet-mesh-3.png)

Attaching our script:

```cs
using Godot;
using Nebula;
using Nebula.Serialization;

public partial class PelletSpawner : NetNode
{
    [Export]
    public MultiMeshInstance3D PelletMeshInstance;
}

```

If we stop and think about what a pellet really is, it's just an image that renders at a Vector3 coordinate. So let's start by creating a NetProperty that can track every pellet position.

```cs
    [NetProperty(NotifyOnChange = true)]
    public NetArray<Vector3> PelletPositions { get; set; } = new(2000, 2000);
```

This creates a new `NetArray` with capacity of 2000, and initialized with 2000 elements. `NetArray` is how Nebula efficiently synchronizes arrays across the network. Importantly, it makes it so changing a single value in the array means only that value is transmitted across the network, rather than the whole array.

Now as our client receives those vector3 values, we'll have them render MultiMeshInstance3D instances at those locations. That's what `NotifyOnChange = true` is for: it calls a function on the client side to handle view updates when the server sends data changes.

```cs
protected virtual void OnNetChangePelletPositions(int tick, Vector3[] deletedValues, int[] changedIndices, Vector3[] addedValues)
    {
        var multimesh = PelletMeshInstance?.Multimesh;
        if (multimesh == null) return;

        if (!multimesh.UseColors)
        {
            multimesh.UseColors = true;
        }

        if (multimesh.InstanceCount < PelletPositions.Length)
        {
            multimesh.InstanceCount = PelletPositions.Length;
        }

        for (int i = 0; i < changedIndices.Length; i++)
        {
            int idx = changedIndices[i];
            var position = PelletPositions[idx];
            multimesh.SetInstanceTransform(idx, new Transform3D(Basis.Identity, position));
            multimesh.SetInstanceColor(idx, CalculatePelletColor(position));
        }
    }
```

As a fun bonus, we'll also be making it so the pellets each have a unique color with the SetInstanceColor.

```cs
    private static Color CalculatePelletColor(Vector3 position)
    {
        const float period = 20f;
        float tX = position.X / period;
        float tZ = position.Z / period;
        tX -= Mathf.Floor(tX);
        tZ -= Mathf.Floor(tZ);
        return new Color(tX, 0.35f, tZ, 1f);
    }
```

Because the color is based on the _position_ of the pellet, and all clients get the same positions, that means all clients will calculate the same color as well. This is a cool example of how we can increase the depth and complexity of a game, without increasing the data we're using.

Now we'll have the server define random positions for all the initial pellets in our `_WorldReady()` function:


```cs
    public override void _WorldReady()
    {
        base._WorldReady();

        if (Network.IsClient)
        {
            return;
        }

        for (int i = 0; i < PelletPositions.Capacity; i++)
        {
            PelletPositions[i] = new Vector3(GD.RandRange(-100, 100), 0.1f, GD.RandRange(-100, 100));
        }
    }
```

The final result of the PelletSpawner script is the following:

```cs
using Godot;
using Nebula;
using Nebula.Serialization;

public partial class PelletSpawner : NetNode
{

    [Export]
    public MultiMeshInstance3D PelletMeshInstance;

    [NetProperty(NotifyOnChange = true)]
    public NetArray<Vector3> PelletPositions { get; set; } = new(2000, 2000);
    protected virtual void OnNetChangePelletPositions(int tick, Vector3[] deletedValues, int[] changedIndices, Vector3[] addedValues)
    {
        var multimesh = PelletMeshInstance?.Multimesh;
        if (multimesh == null) return;

        if (!multimesh.UseColors)
        {
            multimesh.UseColors = true;
        }

        if (multimesh.InstanceCount < PelletPositions.Length)
        {
            multimesh.InstanceCount = PelletPositions.Length;
        }

        for (int i = 0; i < changedIndices.Length; i++)
        {
            int idx = changedIndices[i];
            var position = PelletPositions[idx];
            multimesh.SetInstanceTransform(idx, new Transform3D(Basis.Identity, position));
            multimesh.SetInstanceColor(idx, CalculatePelletColor(position));
        }
    }

    private static Color CalculatePelletColor(Vector3 position)
    {
        const float period = 20f;
        float tX = position.X / period;
        float tZ = position.Z / period;
        tX -= Mathf.Floor(tX);
        tZ -= Mathf.Floor(tZ);
        return new Color(tX, 0.35f, tZ, 1f);
    }

    public override void _WorldReady()
    {
        base._WorldReady();

        if (Network.IsClient)
        {
            return;
        }

        for (int i = 0; i < PelletPositions.Capacity; i++)
        {
            PelletPositions[i] = new Vector3(GD.RandRange(-100, 100), 0.1f, GD.RandRange(-100, 100));
        }
    }
}
```

When we run the game, we'll see the pellet positions and colors are synchronized across clients!

![Create Solution](~/images/big-chungus/chapter-3/pellet-preview.gif)

In the next chapter, we will finish the game with scorekeeping and dynamic gameplay.