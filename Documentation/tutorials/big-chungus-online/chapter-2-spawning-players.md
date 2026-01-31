# Big Chungus Online - Spawning Players

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

That's why "Models" is separate from "Controller". Our "Controller" node synchronizes with the Server, in this jagged, quantized way. We _smooth_ the motion by applying it to our Model gradually. This is called interpolation.

It's possible to implement this for ourselves in a custom way, but to make it easy Nebula provides a `NetTransform3D` to do this for us.

![Create Net Transform](~/images/big-chungus/chapter-2/create-net-transform.png)

![Net Transform Setup](~/images/big-chungus/chapter-2/net-transform-setup.png)

![Net Transform Props](~/images/big-chungus/chapter-2/net-transform-props.png)

So we're telling the `NetTransform3D` to get the Transform data from the Controller node, and apply it to the Model node, with smoothing.

If you're confused, don't worry. NetTransforms can be a bit of a mindfuck, especially to people who are newer to networked game development. For now, the details aren't important. Just know that `NetTransform3D` means "my node's position is synchronized smoothly across the network"

Moving on! We also need a camera to be able to see the player. The tricky thing is, we can't just add a camera node to `Player.tscn`, because then every time a player spawns (e.g. someone logs in) then everyone gets a Camera for that character even if they're not playing it.

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
            GetNode("Model").AddChild(camera);
            camera.LookAt(new Vector3(0, 0, 0));
        }
    }
}
```

A few things are happening here:

1. `_WorldReady` is called after the NetNode finishes "spawning" in the world, and it is setup on the network
2. That code is run by both the server and the client.
3. We check if it is a client running the code (we don't want the server to add a camera) and we make sure the current client owns the NetNode.
4. We instantiate our camera to be positioned above the model and pointing downwards, to look at it from above.

## Main Menu

Real quick, we'll setup an initial screen for the players to drop into once they open the game. This is also where to return when their character is eaten.

Let's put together a quick and simple UI for this purpose (a.k.a, the Main Menu). It can be laid out however we want, so feel free to be creative. The most imporant element is a button to enter the game.

Here's an example layout:

![Player Spawn Manager Setup](~/images/big-chungus/chapter-2/ui.png)

![Player Spawn Manager Setup](~/images/big-chungus/chapter-2/ui-preview.png)

## Spawning And Playing

Now let's go ahead and spawn our players in when they click Play. Going back to `game_arena.tscn` we'll add a new Node called "PlayerSpawner" with a script attached:

![Player Spawn Manager Setup](~/images/big-chungus/chapter-2/player-spawn-manager-setup.png)


```cs
using System.Collections.Generic;
using Godot;
using Nebula;

public partial class PlayerSpawner : NetNode
{
    [Export]
    public PackedScene CharacterScene;

    [Export]
    public Control StartScreen;
    
    [Export]
    public Control ScoreContainer;
}
```

First, let's create handlers for when they click "Play" and "Exit", wiring up the button Pressed signals to their respective functions:

```cs
    public void _OnPlay()
    {
        StartScreen.Visible = false;
        ScoreContainer.Visible = true;
        JoinGame();
    }

    public void _OnExit()
    {
        GetTree().Quit();
    }
```

Now we'll implement JoinGame. This will be how our client tells our server to spawn them in with a new player character scene.

```cs
    [NetFunction(Source = NetFunction.NetworkSources.Client, ExecuteOnCaller = false)]
    public void JoinGame()
    {
    }
```

Here we're defining a `NetFunction`. This is Nebula's version of an RPC. `Source` and `ExecuteOnCaller` parameters are self-explanatory: the RPC can only be initiated by a client, and the caller will not execute the function themselves.

In other words, a client can call `JoinGame()` and it will only run on the server.

This function is very simple: the server must spawn a new player scene for the client.

```cs
    [NetFunction(Source = NetFunction.NetworkSources.Client, ExecuteOnCaller = false)]
    public void JoinGame()
    {   
        var newPlayer = CharacterScene.Instantiate<Player>();
        Network.CurrentWorld.Spawn(newPlayer, inputAuthority: Network.CurrentWorld.NetFunctionContext.Caller);
    }
```

That's it! So to recap:

1. Client clicks "Play!" UI button
2. UI button calls `_OnPlay()` via a "Pressed" signal
3. `_OnPlay()` calls `JoinGame()`
4. `JoinGame` executes on the server
5. The server spawns a new player character scene and sets the "Input Authority" to the client that called JoinGame

Input Authority is essentially the "Owner". It says which client owns the node, meaning they're allowed to send inputs (like movement) to that node. Earlier, we had that conditional `Network.IsCurrentOwner`. This is exactly what that is.

You might be wondering "Can't the client just repeatedly call JoinGame() e.g. by cheats / hacks? Then the server repeatedly spawns them."

Yes, that's definitely true, so we'll need to guard against that. We'll add some simple logic so the Server ensures that it only spawns if the client doesn't already have a player character.

After adding that logic, the final script for player spawning will look like this:

```cs
using System.Collections.Generic;
using Godot;
using Nebula;

public partial class PlayerSpawner : NetNode
{
    [Export]
    public PackedScene CharacterScene;

    [Export]
    public Control StartScreen;
    
    [Export]
    public Control ScoreContainer;
    public Dictionary<UUID, Player> PlayerCharacters { get; } = new();


    [NetFunction(Source = NetFunction.NetworkSources.Client, ExecuteOnCaller = false)]
    public void JoinGame()
    {
        var callerId = NetRunner.Instance.GetPeerId(Network.CurrentWorld.NetFunctionContext.Caller);
        
        // Check if player already exists and is still valid
        if (PlayerCharacters.TryGetValue(callerId, out var existingPlayer) && IsInstanceValid(existingPlayer))
        {
            return;
        }
        
        var newPlayer = CharacterScene.Instantiate<Player>();
        Network.CurrentWorld.Spawn(newPlayer, inputAuthority: Network.CurrentWorld.NetFunctionContext.Caller);
        PlayerCharacters[callerId] = newPlayer;
    }

    public void _OnPlay()
    {
        StartScreen.Visible = false;
        ScoreContainer.Visible = true;
        JoinGame();
    }

    public void _OnExit()
    {
        GetTree().Quit();
    }
}
```

>[!WARNING] Be sure to attach the exported variables in Godot! You'll need to attach the character scene, the start screen UI to hide, and the score container to show when they join the game.

## Level Setup & Running The Game

The last thing we'll do is just add a plane MeshInstance3D to the `game_arena.tscn` for the "floor" of the level that our players can move around on, and a directional light to see.

We can also add a custom texture to the floor, to make it easy to see when the player is moving and make things look more interesting. Here's a simple shader which will render a grid on the plane:


```GLSL
shader_type spatial;
render_mode specular_disabled;

uniform vec3 color_grid : source_color = vec3(0.8, 0.8, 0.8);
uniform vec3 color_tile : source_color = vec3(0.102, 0.102, 0.102);
uniform float small_grid_size : hint_range(1.0, 1000.0) = 60.0;
uniform float large_grid_size : hint_range(1.0, 200.0) = 10.0;
uniform float line_thickness : hint_range(0.001, 0.1) = 0.02;
uniform float fade_start_distance : hint_range(0.0, 200.0) = 25.0;
uniform float fade_end_distance : hint_range(0.0, 500.0) = 30.0;

float grid_line(vec2 uv, float grid_size, float thickness) {
	vec2 grid_uv = uv * grid_size;
	vec2 f = fract(grid_uv);
	float dist_to_line = min(min(f.x, 1.0 - f.x), min(f.y, 1.0 - f.y));
	return 1.0 - smoothstep(0.0, thickness, dist_to_line);
}

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float small_line = grid_line(UV, small_grid_size, line_thickness);
	float large_line = grid_line(UV, large_grid_size, line_thickness);
	float distance_to_camera = length(CAMERA_POSITION_WORLD - world_pos);
	float fade = smoothstep(fade_start_distance, fade_end_distance, distance_to_camera);
	float line = max(small_line * (1.0 - fade), large_line * fade);

	ALBEDO = mix(color_tile, color_grid, clamp(line, 0.0, 1.0));
	ROUGHNESS = 1.0;
}
```

![Level Floor Setup](~/images/big-chungus/chapter-2/level-floor-setup.png)

That's it! Now run the game and we should see something similar to the following:

![First Game Run](~/images/big-chungus/chapter-2/players.gif)

The player characters and game arena are all setup. In the next chapter, we'll handle the core gameplay: player inputs (movement); points; character growth.