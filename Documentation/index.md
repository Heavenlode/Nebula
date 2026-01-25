# Nebula Documentation

Welcome to the Nebula networking framework for Godot.

Nebula is a fun, efficient, extensible networking framework designed to make multiplayer game development in Godot a breeze. It's built from the ground up for Godot's C# support, giving you a clean, modern API that feels natural to use.

> Disclaimer: this document was written about 50% by a human. The author didn't have time to sit down and write it all out, though someday maybe that will change. It should still be very valuable to you if you are interested in these details. Hail our AI overlords!

## Quick Start

Get up and running with Nebula in minutes:

1. [Introduction](getting-started/introduction.md) - Learn the core concepts
2. [Network Lifecycle](getting-started/network-lifecycle.md) - Understand how data flows
3. [Tutorials](tutorials/snake/create-a-snake.md) - Build your first networked game

## What Makes Nebula Different?

### Server-Authoritative by Design
The server is always the source of truth. Clients can't cheat by modifying game state directly—they send inputs, and the server decides what happens. This makes your game secure without extra work.

### Tick-Based Synchronization
Nebula runs on a fixed tick rate (default 30 ticks/second), giving you deterministic, predictable networking. No more worrying about frame rate differences between players.

### Automatic State Sync
Mark a property with `[NetProperty]` and it automatically syncs to clients. That's it. No manual serialization, no networking boilerplate.

```csharp
[NetProperty]
public int Health { get; set; }
```

### Interest Management Built-In
Control exactly what each player can see. Perfect for hidden information (card games), fog of war, or just optimizing bandwidth by not sending irrelevant data.

```csharp
// Only the owner sees their money
[NetProperty(InterestMask = (long)InterestLayers.Owner)]
public int Money { get; set; }
```

### Nested Scene Replication
Spawn a player with an inventory, and all the items inside automatically replicate too. Nebula tracks your scene hierarchy and keeps everything in sync.

## Core Features

- **Efficient**: Tick-based updates with smart dirty checking—only changed properties are sent
- **Extensible**: Custom serializers, authentication, and interest layers
- **Godot Native**: Works with Node, Node2D, and Node3D seamlessly
- **Easy to Use**: Simple attributes handle most networking needs
- **Input System**: Type-safe input handling with built-in buffering
- **World Isolation**: Run multiple game instances (maps, dungeons) on one server

## How It Works (30-Second Version)

1. **Server** runs the game logic and owns the "real" game state
2. **Clients** see a copy of that state and send inputs (like "move left")
3. Every tick, the server processes inputs, updates state, and broadcasts changes
4. Clients receive updates and can react (play animations, show UI, etc.)

That's the mental model.