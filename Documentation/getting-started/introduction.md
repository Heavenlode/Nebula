# Introduction

Nebula is a fun, efficient, extensible networking framework for Godot.

At the base of any Nebula game/network is the NetRunner. The details of this node doesn't really matter much here (although more technical deep-dives are available if you are interested).

For now, all you need to know is the NetRunner handles all client connections, data transmission, etc. It is the lowest abstraction level and exists as a global singleton.

![NetRunner](~/images/Pasted-image-20250510132846.png)

## WorldRunner
Inside the NetRunner are one or more "Worlds". Each World represents some part of the game that is isolated from other parts. For example, different maps, dungeon instances, etc. Worlds are dynamically created by calling `CreateWorld` on the NetRunner.

Each World is run/managed by what is called a WorldRunner. Worlds cannot directly interact with each other and do not share state.

Players only exist in one World at a time, so it can be helpful to think of the clients as being connected to a World directly.

![WorldRunner](~/images/Pasted-image-20250510140902.png)

Just as in a normal Godot game where you have an "initial scene" that first opens when running the game, the WorldRunner also has an initial scene when you enter the World. In this way, you can think of the Nebula server as being able to run multiple "games" simultaneously.

When a client connects to an Nebula server, the NetRunner assigns that client to a World and tells the client what the scene is. The client then sets things up on their end to match the server.

![Client Connection](~/images/Pasted-image-20250510140558.png)
![Client Setup](~/images/Pasted-image-20250510140619.png)

> [!NOTE]
> Despite the server potentially having multiple WorldRunners, the client will only ever have one WorldRunner--for the world that the Server put them in!

## NetNode / NetScene

The root scene of a WorldRunner is a kind of node called a "NetNode."

A NetNode is a Node that is a part of the network lifecycle, i.e. it can synchronize its state across the network. (The Network Lifecycle chapter talks more about this.)

When a NetNode is the root of a Scene (a `.tscn` file), then it is said to be a NetScene. The WorldRunner root scene must be a NetScene.

![NetScene](~/images/Pasted-image-20250510143316.png)

At this point, you might be wondering "Huh? NetNode vs. NetScene? What's the difference?"

Here's the simple breakdown:

**Static Children** are NetNodes that exist inside a scene file but are *not* their own `.tscn`. They're baked into the parent scene and can't be added or removed at runtime.

**Dynamic Children** are NetScenes (their own `.tscn` files) that are nested inside another scene. These *can* be spawned or despawned at runtime, and Nebula automatically replicates them to clients.

For example, imagine a `Player.tscn` that contains:
- `Level1` (Node3D) — just a regular node
- `Level2/Level3` (NetNode3D) — a **static child**, part of the player
- `Level2/Level3/Item` (Item.tscn) — a **dynamic child**, its own NetScene

When a Player spawns, Nebula automatically:
1. Spawns the Player on the client
2. Discovers that Item.tscn is nested inside
3. Replicates Item to the client with matching state

If the server later despawns that Item, the client's copy gets removed too. It all happens automatically.

**The rules:**
* Only NetScenes can be spawned/despawned dynamically
* Static NetNodes live and die with their parent scene
* NetScenes can only be children of other NetNodes/NetScenes (not random Nodes)

>[!NOTE]
>More technical details are available. TL;DR it's part of how the network is optimized to be low bandwidth and highly efficient.

## Conclusion
That's the absolute bare-bones basics of Nebula concepts. In the next chapter, we'll go over the Network Lifecycle, including an overview of how data is sent across the network.
