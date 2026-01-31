# Big Chungus Online - Dynamic Gameplay

This chapter covers:

* Score tracking
* Dynamic gameplay

> [!NOTE]
> This part of the tutorial is more code-heavy, but the difficulty level is not too high!

## Score and Size

When a player character touches a pellet, the pellet should disappear, respawn elsewhere, and the player should gain a point.

To make the game more interesting, when the player gains a point, they should also grow in size and slow down slightly. This makes it so bigger players can consume smaller players, but can be outrun by them too.

To do this, first we'll define the logic on the `Player.cs` script for score handling.

We'll add our networked Score variable and a reference to the Score UI label so the player can see their score.

```cs
    [NetProperty(NotifyOnChange = true)]
    public int Score { get; set; } = 0;

    [Export]
    private MeshInstance3D _model;

    public Label ScoreLabel;
```

Be sure to wire up `_model` in the Godot editor! We'll also handle what happens on the client side when it sees the score change:

```cs
    protected virtual void OnNetChangeScore(int tick, int oldValue, int newValue)
    {
        if (Network.IsCurrentOwner)
        {
            ScoreLabel?.Text = $"Score: {newValue}";
        }
        UpdateModelScale();
    }
```

The UpdateModelScale happens on all player nodes as they gain score, regardless if the client owns it or not. That is what we want.

What we _don't_ want is for our score text to change when _another person's score_ changes. That's why we wrap that part in IsCurrentOwner. Otherwise every time another player gains points, our score text would suddenly show their score!

Then define `UpdateModelScale()`

```cs
    private const float ScoreSizeDivisor = 50f;
    public float GetSizeScale()
    {
        return BaseSizeScale + Score / ScoreSizeDivisor;
    }
    private void UpdateModelScale()
    {
        float scale = GetSizeScale();
        _model.Scale = new Vector3(scale, scale, scale);
    }
```

There's a small problem here though. When the model grows, it happens suddenly, which feels unnatural. It  feels particularly bad for our camera, which jumps position and disorients the player.

So, let's actually interpolate that size change in our `_Process` script. This is a perfect demonstration of how the client view can run its own logic in reaction to the server's changes.

```cs
    private Vector3 _targetScale = Vector3.One;
    private const float ScaleLerpSpeed = 8f;

    private void UpdateModelScale()
    {
        float scale = GetSizeScale();
        _targetScale = new Vector3(scale, scale, scale);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        if (_model == null)
        {
            return;
        }

        float t = 1f - Mathf.Exp(-ScaleLerpSpeed * (float)delta);
        _model.Scale = _model.Scale.Lerp(_targetScale, t);
    }
```

Finally in our `_WorldReady()` script, we'll wire up the label and initialize the text:

```cs
    ScoreLabel = Network.CurrentWorld.RootScene.RawNode.GetNode<Label>("%ScoreLabel");
    ScoreLabel?.Text = $"Score: {Score}";
```

The resulting `Player.cs` should be like this:


```cs
using Godot;
using Nebula;

public partial class Player : NetNode
{

    [Export]
    private MeshInstance3D _model;
    public Label ScoreLabel;

    [NetProperty(NotifyOnChange = true)]
    public int Score { get; set; } = 0;
    protected virtual void OnNetChangeScore(int tick, int oldValue, int newValue)
    {
        if (Network.IsCurrentOwner)
        {
            ScoreLabel?.Text = $"Score: {newValue}";
        }
        UpdateModelScale();
    }

    public override void _WorldReady()
    {
        base._WorldReady();

        if (Network.IsClient && Network.IsCurrentOwner)
        {
            ScoreLabel = Network.CurrentWorld.RootScene.RawNode.GetNode<Label>("%ScoreLabel");
            ScoreLabel?.Text = $"Score: {Score}";
            var camera = new Camera3D();
            camera.Position = new Vector3(0, 10, 0);
            GetNode("Model").AddChild(camera);
            camera.LookAt(new Vector3(0, 0, 0));
        }
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        if (_model == null)
        {
            return;
        }

        float t = 1f - Mathf.Exp(-ScaleLerpSpeed * (float)delta);
        _model.Scale = _model.Scale.Lerp(_targetScale, t);
    }

    private const float ScoreSizeDivisor = 50f;
    private const float BaseSizeScale = 1f;
    private const float ScaleLerpSpeed = 8f;
    public float GetSizeScale()
    {
        return BaseSizeScale + Score / ScoreSizeDivisor;
    }

    private Vector3 _targetScale = Vector3.One;
    private void UpdateModelScale()
    {
        float scale = GetSizeScale();
        _targetScale = new Vector3(scale, scale, scale);
    }
}
```

## Player Speed

The other thing we wanted to do was make the player slow down the bigger they are. So, head over to the `PlayerController.cs`. We're about to make our _final changes to this file!_ We're almost there.

```cs
    [Export]
    public float BaseSpeed { get; set; } = 0.50f;

    [Export]
    public float ScoreSlowdownFactor { get; set; } = 0.05f;

    [Export]
    private Player _player;
```

We track our base speed, how fast the player slows down based on size, and a reference to the Player itself so we can use the score accordingly.

Now we simply update _NetworkProcess with our new logic:

```cs
    var score = _player?.Score ?? 0;
    var speed = BaseSpeed / (1f + Mathf.Max(0, score) * ScoreSlowdownFactor);
    Position += Direction * speed;
```

The final `PlayerController.cs` looks like this:

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
    [Export]
    public float BaseSpeed { get; set; } = 0.50f;

    [Export]
    public float ScoreSlowdownFactor { get; set; } = 0.05f;

    [Export]
    private Player _player;

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
        if (!Network.IsWorldReady) return;

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

        var score = _player?.Score ?? 0;
        var speed = BaseSpeed / (1f + Mathf.Max(0, score) * ScoreSlowdownFactor);
        Position += Direction * speed;
        Position = new Vector3(Mathf.Clamp(Position.X, -100, 100), Position.Y, Mathf.Clamp(Position.Z, -100, 100));
    }
}
```

To recap, here's what would happen when a player collides with a pellet:

1. Player touches pellet (Need to implement)
2. Pellet disappears and respawns (Need to implement)
3. Score increases (Need to implement)
4. Player model grows (✅ Done)
5. Player sees their new score (✅ Done)
6. Player slows down (✅ Done)

Halfway done. The last part is the actual pellet collection / collision mechanism.

To do this, we need to check the distance from every player to every pellet. There are multiple correct ways to do this, so feel free to come up with your own approach. For this tutorial, we're going to centralize this in a new `GameScoreManager` node.

> [!NOTE]
> We'll be using this node in the _next chapter_ to also calculate collisions between players.

## Pellet Collection

```cs
using Godot;
using Nebula;
using System.Collections.Generic;

public partial class GameScoreManager : NetNode
{
    [Export]
    public PelletSpawner PelletSpawner;
    public List<Player> Players { get; } = new();
    public override void _NetworkProcess(int tick)
    {
        base._NetworkProcess(tick);

        if (Network.IsClient)
        {
            return;
        }

        CheckPelletCollisions();
    }

    private void CheckPelletCollisions()
    {
    }
}
```

Very simply, CheckPelletCollisions should check the distance of every player to every pellet.

> [!NOTE]
> For the computer science geeks, this is an `O(n * m)` operation, so it's not the most efficient. It's not really a big deal for our purposes though, especially since vector math isn't that expensive.

```cs
    foreach (var player in Players)
    {
        var playerPos = player.GetWorldPosition();
        float collisionRadius = player.GetCollisionRadius();

        for (int i = 0; i < pelletPositions.Length; i++)
        {
            var pelletPos = pelletPositions[i];
            float distanceSquared = (playerPos.X - pelletPos.X) * (playerPos.X - pelletPos.X)
                                    + (playerPos.Z - pelletPos.Z) * (playerPos.Z - pelletPos.Z);

            if (distanceSquared < collisionRadius)
            {
                player.Score++;
                PelletSpawner.RespawnPellet(i);
            }
        }
    }
```

The final `GameScoreManager` code would be the following:

```cs

using Godot;
using Nebula;
using System.Collections.Generic;

public partial class GameScoreManager : NetNode
{
    [Export]
    public PelletSpawner PelletSpawner;
    public List<Player> Players { get; } = new();
    public override void _NetworkProcess(int tick)
    {
        base._NetworkProcess(tick);

        if (Network.IsClient)
        {
            return;
        }

        CheckPelletCollisions();
    }

    private void CheckPelletCollisions()
    {
        if (PelletSpawner == null) return;

        var pelletPositions = PelletSpawner.PelletPositions;

        foreach (var player in Players)
        {
            var playerPos = player.GetWorldPosition();
            float collisionRadius = player.GetCollisionRadius();

            for (int i = 0; i < pelletPositions.Length; i++)
            {
                var pelletPos = pelletPositions[i];
                float distanceSquared = (playerPos.X - pelletPos.X) * (playerPos.X - pelletPos.X)
                                      + (playerPos.Z - pelletPos.Z) * (playerPos.Z - pelletPos.Z);

                if (distanceSquared < collisionRadius)
                {
                    player.Score++;
                    PelletSpawner.RespawnPellet(i);
                }
            }
        }
    }
}
```

Note that the GameScoreManager has a list of Players. We'll need to make sure the Player nodes each populate that as they spawn in, in `Player.cs` -> `_WorldReady`. 

```cs
    public override void _WorldReady()
    {
        base._WorldReady();

        if (Network.IsClient && Network.IsCurrentOwner)
        {
            ScoreLabel = Network.CurrentWorld.RootScene.RawNode.GetNode<Label>("%ScoreLabel");
            ScoreLabel?.Text = $"Score: {Score}";
            var camera = new Camera3D();
            camera.Position = new Vector3(0, 10, 0);
            GetNode("Model").AddChild(camera);
            camera.LookAt(new Vector3(0, 0, 0));
        }

        if (Network.IsServer)
        {
            var scoreManager = Network.CurrentWorld.RootScene.RawNode.GetNode<GameScoreManager>("GameScoreManager");
            scoreManager?.Players.Add(this);
        }
    }
```

While we're there, we'll want to define `GetWorldPosition` and `GetCollisionRadius` helper methods as well.

```cs
    public float GetCollisionRadius()
    {
        return GetSizeScale();
    }

    [Export]
    public Node3D PositionNode;
    public Vector3 GetWorldPosition()
    {
        return PositionNode?.GlobalPosition ?? Vector3.Zero;
    }
```

Do you remember what the `PositionNode` would be? Our first instinct might be the root Player node, or the Model node. Actually, it is the `PlayerController` node. Remember, Model only has the _client's interpretation_ of the position. The PlayerController has the _true server position_.

![Player Node](~/images/big-chungus/chapter-4/player.png)

Beautiful! We're almost ready to play the game. The last thing we need to do is make the pellet disappear and respawn.

Remember, our pellets are simply represented as an array of Vector3. That means the disappear/respawn action can be combined.

Back in our `PelletSpawner.cs` file:

```cs
    public void RespawnPellet(int index)
    {
        if (index >= 0 && index < PelletPositions.Length)
        {
            PelletPositions[index] = new Vector3(GD.RandRange(-100, 100), 0.1f, GD.RandRange(-100, 100));
        }
    }
```

It's that simple! To the player, it _appears_ that pellets are being consumed and spawning randomly, but in our server-side code, we're actually just _moving_ the pellets elsewhere. That means the game always has exactly 2000 pellets at any given time.

PelletPositions is a `NetProperty`, which means that when the server changes a value in PelletPositions, the clients receive that new value.

How do we handle that new value? We already wrote the code for it. Remember `OnNetChangePelletPositions`? That's where it happens (the code we already wrote in the previous chapter):

```cs
for (int i = 0; i < changedIndices.Length; i++)
{
    int idx = changedIndices[i];
    var position = PelletPositions[idx];
    multimesh.SetInstanceTransform(idx, new Transform3D(Basis.Identity, position));
    multimesh.SetInstanceColor(idx, CalculatePelletColor(position));
}
```

Any time a pellet position changes... we move it, and change the color.

![Collect Pellets](~/images/big-chungus/chapter-4/collect-pellets.gif)

There we have it! The Big Chungus is almost complete. The last things we need to do, which we'll cover in the final chapter, is players consuming each other and resetting the game.