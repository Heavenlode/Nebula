# Big Chungus Online - Building The Game

This chapter covers:
* Setting up game players
* Basic game mechanics

# Game Character Setup

In the last chapter, we got the Nebula foundation setup and running. Now we can start building the game. The best place to start that is with the player characters.

We're about to make a box-shaped player character that moves steadily in one direction.

First, we'll create a new `Node` scene `player.tscn`. In this scene, we'll add two child nodes: a `MeshInstance3D` named "Model", and a `Node` named "Controller". It will soon be clear why we need two different nodes.

![Player Setup](~/images/big-chungus/chapter-2/player-setup.png)

On the root Player node, we'll add a new, blank NetNode script `Player.cs`:

```cs
using Nebula;

public partial class Player : NetNode
{
}
```

Now let's get the foundational movement functionality by attaching a new `PlayerController.cs` script to the Controller node.

```cs
using Godot;
using Nebula;

public partial class PlayerController : NetNode3D
{
    public Vector3 Direction { get; set; } = new Vector3(0.10f, 0, 0);

    public override void _NetworkProcess(int tick)
    {
        base._NetworkProcess(tick);
        Position += Direction;
    }
}
```

`_NetworkProcess` runs on both the client and the server, for every server `Tick`. Nebula's default server tick rate is 30hz (30 ticks per second), which is a fairly common speed for game servers (For example, it's approximately the tick rate of [League of Legends](https://wiki.leagueoflegends.com/en-us/Tick_and_updates#Server_ticks)).

It means that 30 times per second, the server runs physics logic, like our player movement. The problem is that this will create a jagged, stuttery movement for the Client, since their FPS/refresh rate is likely going to be much faster than that.

That's why "Models" is separate from "Controller". Our "Controller" node synchronizes with the Server in this jagged way, and we _smooth_ the motion by applying it to our Model gradually. This is called interpolation.

It's possible to implement this for ourselves in a custom way, but to make it easy Nebula provides a `NetTransform3D` to do this for us.

![Create Net Transform](~/images/big-chungus/chapter-2/create-net-transform.png)

![Net Transform Setup](~/images/big-chungus/chapter-2/net-transform-setup.png)

![Net Transform Props](~/images/big-chungus/chapter-2/net-transform.props.png)

So we're telling the `NetTransform3D` to get the Transform data from the Controller node, and apply it to the Model node, with smoothing.

We also need a camera to be able to see the player. The tricky thing is, we can't just add a camera node to Player.tscn because then every time a player spawns (e.g. someone logs in) then everyone gets a Camera for that character even if they're not playing it.

We only want a camera if we _own_ the node. So, let's go ahead and add that in our Player NetNode.

```cs
using Godot;
using Nebula;

public partial class Player : NetNode
{
    public override void _WorldReady() {
        base._WorldReady();

        if (Network.IsClient && Network.IsCurrentOwner) {
            var camera = new Camera3D();
            camera.Position = new Vector3(0, 10, 0);
            camera.LookAt(new Vector3(0, 0, 0));
            GetNode("Model").AddChild(camera);
        }
    }
}
```

A few things are happening here:

1. `_WorldReady` is called after the NetNode finishes "spawning" in the world, and it is setup on the network
2. That code is run by both the server and the client.
3. We check if it is a client running the code (we don't want the server to add a camera) and we make sure the current client owns the NetNode.
4. We instantiate our camera to be positioned above the model and pointing downwards, to look at it from above.

## Spawning And Playing

Now let's go ahead and spawn our players in. Going back to `game_arena.tscn` we'll add a new Node called "PlayerSpawnManager" with a script attached:

![Player Spawn Manager Setup](~/images/big-chungus/chapter-2/player-spawn-manager-setup.png)

```cs
using Godot;
using Nebula;

public partial class PlayerSpawnManager : NetNode
{
    [Export]
    public PackedScene CharacterScene;
    public override void _WorldReady()
    {
        base._WorldReady();
        
        if (Network.IsServer) {
            Network.CurrentWorld.OnPlayerJoined += _OnPlayerJoined;
        }
    }

    private void _OnPlayerJoined(UUID peerId)
    {
        var playerCharacter = CharacterScene.Instantiate<Player>();
        Network.CurrentWorld.Spawn(playerCharacter, inputAuthority: NetRunner.Instance.Peers[peerId]);
    }
}
```

Be sure to attach the character scene:
![Attach Character Scene](~/images/big-chungus/chapter-2/character-scene.png)

On the server side, the PlayerSpawnManager is hooking into the World's "player join" event, and spawning a player.tscn for them.

Notice the `inputAuthority` part? That's what designates who owns the node. Earlier in our `Player.cs` script we had `Network.IsCurrentOwner`. We're saying the peer who just joined is the owner if this newly spawned node. We'll see more about what that means shortly.

## Level Setup & Running The Game

Let's get ready to run the game. The last thing we'll do is just add a plane MeshInstance3D to the `game_arena.tscn` for the "floor" of the level that our players can move around on. We can also add a custom texture to it, to make it easy to see when the player is moving. Here's a simple shader which will render a grid on the plane:


```
shader_type spatial;

uniform vec3 color_white : source_color = vec3(0.8, 0.8, 0.8);
uniform vec3 color_navy : source_color = vec3(0.0, 0.0, 0.5);
uniform float grid_size : hint_range(1.0, 1000.0) = 500.0;

void fragment() {
	vec2 scaled_uv = UV * grid_size;
	vec2 cell = floor(scaled_uv);
	float checker = mod(cell.x + cell.y, 2.0);
	vec3 color = mix(color_white, color_navy, checker);
	
	ALBEDO = color;
}
```

![Level Floor Setup](~/images/big-chungus/chapter-2/level-floor-setup.png)

Let's also add a simple directional light with shadow casting.

![Light Setup](~/images/big-chungus/chapter-2/light-setup.png)

That's it! Now run the game and we should see something similar to the following:

![First Game Run](~/images/big-chungus/chapter-2/game-animation.gif)

The player characters and game arena are all setup. In the next chapter, we'll handle player inputs (movement), and points / character growth.