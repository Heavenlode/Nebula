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

When a NetNode is the root of a Scene, then it is said to be a NetScene. The WorldRunner root scene must be a NetScene.

![NetScene](~/images/Pasted-image-20250510143316.png)

At this point, you might be wondering "Huh? NetNode vs. NetScene? What's the difference?"

For now, the main thing to know is this:
* Net*Scenes* are instantiated dynamically
* Net*Nodes* are not. They exist statically inside NetScenes

In other words, NetNodes cannot be individually instantiated while the game is running; only NetScenes can.

Also, when you do instantiate a NetScene, it can only be added as a child of another NetScene or a NetNode. You can't instantiate a NetScene to be a child of some non-net Node. Thems the rules!

>[!NOTE]
>More technical details are available. TL;DR it's part of how the network is optimized to be low bandwidth and highly efficient.

## Conclusion
That's the absolute bare-bones basics of Nebula concepts. In the next chapter, we'll go over the Network Lifecycle, including an overview of how data is sent across the network.
