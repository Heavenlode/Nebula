# Network Lifecycle

In order to understand the network lifecycle, you first need to understand how the Server and Client are different from each other--despite both running the same game.

## Server characteristics
* Runs game logic.
	* e.g. physics, collisions, scoring, etc.
	* Does not render any display or view.
* Alters game state.
	* Mutates the NetProperties on the NetNodes.
* Receives and processes game inputs from clients.
* Maintains the entire game state
	* Sends out changes to all clients.

## Client characteristics
* Renders views based on data received from the server.
	* Does not run any game logic such as physics, collisions, etc.
* Cannot directly alter game state.
	* Manually changing the NetProperties on NetNodes has no effect.
* Sends inputs/actions to the server
	* e.g. "move left", "move right"
* Unaware of entire game state--only knows what the server tells it.
	* Receives changes from the server and applies it to its own game state.

With this understanding, we can now break these down one at a time.

## Game Logic / Tick Processing

Game logic is only executed by the server. For example, a game character NetNode might literally looks something like this:

```cs
using Godot;
using Nebula;

public partial class PlayableCharacter : NetNode3D
{
	[NetProperty]
	public int Money { get; set; }

	public override void _NetworkProcess(int _tick)
	{
		base._NetworkProcess(_tick);
		if (NetRunner.Instance.IsServer)
		{
			Money += 1;
		}
	}
}
```

`_NetworkProcess` is fairly similar to a Node's `_Process` with some key differences:
* For the server, it runs on a fixed interval (known as a "Tick")
	* In the final step of a tick, the server sends the game state changes to all connected clients.
	* e.g. if the game is running at 30 TPS (ticks-per-second), then the clients will receive updates up to *30 times per second* (depending on network conditions).
* For the client, this runs _whenever it receives a tick from the server_.

In the code example above, the logic code to increment the Money value is only run if the NetRunner is a server.

>[!NOTE]
>_(this note isn't strictly necessary to know. if it's confusing or overwhelming, feel free to disregard)_
>
>The game state is managed by the WorldRunner, because every World has its own unique state and logic. Everything listed earlier under the "Server characteristics" is what each WorldRunner does!

You might be wondering "What's stopping the client from just changing the money value themselves?"

So if the function was instead like:
```cs
public override void _NetworkProcess(int _tick)
{
	base._NetworkProcess(_tick);
	if (NetRunner.Instance.IsServer)
	{
		Money += 1;
	} else if (NetRunner.Instance.IsClient)
	{
		Money += 100;
	}
}
```

Is the client now cheating and getting rich? Nope, because the server doesn't know/care about the change, and no other clients/players will know about it either.

So the client will temporarily see its money jump +100, and then on the next tick (the next update it receives from the server) it will be immediately overwritten by the real value that the server sends it.

Wondering what the client even uses `_NetworkProcess` for? Whenever the function is run, it means the client has an update of the latest game state, so it can do things to react to game state changes:

```cs
using Godot;
using Nebula;

public partial class PlayableCharacter : NetNode3D
{
	[NetProperty]
	public int Money { get; set; }

	public bool IsRich = false;

	public override void _NetworkProcess(int _tick)
	{
		base._NetworkProcess(_tick);
		if (NetRunner.Instance.IsServer)
		{
			Money += 1;
		}
		
		if (NetRunner.Instance.IsClient)  {
			if (Money > 100) {
				if (!IsRich) {
					IsRich = true;
					ShowRichCelebration();
				} 
			} else {
				IsRich = false;
			}
		}
	}
}
```

In this example, the client runs some code to determine if the player's latest Money value is high enough to be considered "Rich".

If they are, then they can trigger some special view/UI actions such as playing particle emitters, showing popup dialogs, etc. (Things that don't happen on the server.)

>[!NOTE]
> Unlike the `Money` property, `IsRich` is not a `NetProperty`. This means that it doesn't live in the game network--so it never receives updates from the server--and the client can mutate it freely.

## Inputs
If the client cant make any changes to the game state, then how do they even play the game?

That's what inputs and NetFunctions are for. Nebula supports sending an "Input"

## State "awareness" (or, Interest Management)
Interest Management is a feature for the server to decide _who_ gets to see _what._ Think about a game with hidden information: another player's money or items; cards in a card game; "fog of war"; etc.

The server decides what clients are allowed to see--what nodes and property changes they are notified of. The server can disable a client's "interest" in a NetProperty, which means that client will no longer receive updates about that NetProperty.

To hide the Money from all clients:
```cs
[NetProperty(InterestMask = (long)InterestLayers.None)]
public int Money { get; set; }
```

Now whenever Money changes, the server will not send those updates to the connected clients. It is private to only the server.

To show the money to all clients:
```cs
[NetProperty(InterestMask = (long)InterestLayers.Everyone)]
public int Money { get; set; }
```

Now the server will send the new money value to all clients on every tick. It's the default `InterestMask` actually, so you can just omit it entirely for the same effect.

Perhaps the most important:
```cs
[NetProperty(InterestMask = (long)InterestLayers.Owner)]
public int Money { get; set; }
```

Now only the player who "owns" a NetNode will receive updates about that property.

>[!TIP]
>You can also designate your own layers beyond these three. Think of it like a 'collision layer' in Godot. It's essentially a clean slate of layers for you to customize however you want. This can be useful for team games, factions, etc.

## Lifecycle Overview
