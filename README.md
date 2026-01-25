# Nebula

**Multiplayer netcode for Godot C#**

Nebula is a tick-based, server-authoritative networking framework that makes building responsive online games in Godot straightforward. It handles the hard parts—client-side prediction, rollback, interpolation, and state synchronization—so you can focus on your game.

## Table of Contents

- [Why Nebula?](#why-nebula)
- [Quick Example](#quick-example)
- [Features](#features)
  - [Core Networking](#core-networking)
  - [Client-Side Prediction](#client-side-prediction)
  - [Visual Smoothing](#visual-smoothing)
  - [Developer Experience](#developer-experience)
  - [Performance](#performance)
  - [Architecture](#architecture)
- [Installation](#installation)
  - [Requirements](#requirements)
  - [Setup](#setup)
- [Core Concepts](#core-concepts)
  - [Network Nodes](#network-nodes)
  - [Network Properties](#network-properties)
  - [Input Handling](#input-handling)
  - [Spawning Entities](#spawning-entities)
- [Comparison](#comparison)
- [Documentation](#documentation)
- [Roadmap](#roadmap)
- [Community](#community)
- [Contributing](#contributing)

## Why Nebula?

Building multiplayer games is hard. Building multiplayer games that *feel good* in Godot is even harder. Nebula solves the core challenges:

- **Instant responsiveness** — Client-side prediction means players see immediate feedback, not 100ms of input lag
- **Cheat resistance** — Server-authoritative model where the server is always the source of truth
- **Smooth visuals** — Automatic interpolation for non-owned entities eliminates jitter
- **Simple API** — Annotate properties with `[NetProperty]` and let the source generator handle the rest
- **Zero-allocation hot paths** — Carefully optimized to avoid GC pressure; no lag spikes from garbage collection during gameplay

## Quick Example

```csharp
using Godot;
using Nebula;

public partial class Player : NetNode3D
{
    public Player()
    {
        Network.InitializeInput<PlayerInput>();
    }

    // Synced to all clients, with interpolation for smooth movement
    [NetProperty(Interpolate = true, Predicted = true)]
    public Vector3 Position { get; set; }
    
    // Required for predicted properties - defines misprediction threshold
    public float PositionPredictionTolerance { get; set; } = 2f;

    // Server-only state - only visible to specific players
    [NetProperty(InterestMask = 0x02)]
    public int SecretScore { get; set; }

    // Called when Position changes on clients
    partial void OnNetChangePosition(int tick, Vector3 oldVal, Vector3 newVal)
    {
        GD.Print($"Moved to {newVal}");
    }

    public override void _NetworkProcess(int tick)
    {
        // Both server AND owning client run this for prediction
        if (NetRunner.Instance.IsServer || Network.IsCurrentOwner)
        {
            ref readonly var input = ref Network.GetInput<PlayerInput>();
            Position += new Vector3(input.MoveX, 0, input.MoveY) * Speed;
        }
    }
}
```

## Features

### Core Networking
- **Tick-based synchronization** — Deterministic simulation at configurable tick rates (default 30Hz)
- **Server-authoritative** — Single source of truth prevents cheating and ensures consistency
- **Input authority** — Clients control specific entities; server validates and processes inputs
- **Interest management** — Fine-grained control over what data each player receives

### Client-Side Prediction
- **Instant feedback** — Players see results immediately, not after round-trip latency
- **Automatic rollback** — When predictions are wrong, Nebula corrects seamlessly
- **Configurable tolerance** — Tune misprediction thresholds per-property

### Visual Smoothing
- **Interpolation** — Smooth movement for entities you don't control
- **Prediction + Interpolation** — Owned entities predict, others interpolate—same property, automatic switching

### Developer Experience
- **Source generators** — No boilerplate; annotate properties and methods
- **Compile-time validation** — Errors like missing handlers caught at build time
- **Typed inputs** — Zero-allocation input structs with full type safety
- **Partial methods** — Hook into network events with generated partial methods

### Performance
- **GC-friendly design** — Hot paths avoid allocations; no lag spikes from .NET garbage collection
- **Struct-based serialization** — Inputs and state use unmanaged structs, not heap objects
- **Pooled buffers** — Network buffers are reused, not allocated per-packet
- **No boxing** — PropertyCache union type avoids boxing value types

### Architecture
- **Multiple worlds** — Run separate game instances (lobbies, matches) in one server process
- **Flexible serialization** — Built-in support for primitives, vectors, quaternions, and custom types
- **ENet transport** — Reliable UDP with automatic connection management

## Installation

### Requirements
- Godot 4.5+ with .NET support
- .NET 10.0+

### Setup

1. **Add Nebula to your project**
   
   Copy the `addons/Nebula` folder into your project's `addons` directory.

2. **Import in your .csproj**
   
   ```xml
   <Project Sdk="Godot.NET.Sdk/4.5.1">
     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
     </PropertyGroup>
     
     <Import Project="addons\Nebula\Nebula.props" />
   </Project>
   ```

3. **Enable the plugin**
   
   In Godot: Project → Project Settings → Plugins → Enable "Nebula"

4. **Build your project**
   
   The source generators will create the necessary code on first build.

## Core Concepts

### Network Nodes

Inherit from `NetNode`, `NetNode2D`, or `NetNode3D` to create networked entities:

```csharp
public partial class Enemy : NetNode3D
{
    [NetProperty]
    public int Health { get; set; } = 100;
}
```

### Network Properties

Mark properties for synchronization with `[NetProperty]`:

```csharp
[NetProperty]                          // Basic sync
[NetProperty(NotifyOnChange = true)]   // Triggers OnNetChange{Name}() on change
[NetProperty(Interpolate = true)]      // Smooth interpolation for non-owners
[NetProperty(Predicted = true)]        // Client-side prediction for owners
[NetProperty(InterestMask = 0x04)]     // Only sync to players with this interest layer
```

### Input Handling

Define input structs and process them on both server and client:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayerInput
{
    public float MoveX;
    public float MoveY;
    public bool Jump;
}

// In your node:
public override void _PhysicsProcess(double delta)
{
    if (Network.IsCurrentOwner && !NetRunner.Instance.IsServer)
    {
        Network.SetInput(new PlayerInput
        {
            MoveX = Input.GetAxis("left", "right"),
            MoveY = Input.GetAxis("up", "down"),
            Jump = Input.IsActionPressed("jump")
        });
    }
}
```

### Spawning Entities

```csharp
// Server-side spawning with input authority
var player = playerScene.Instantiate<Player>();
worldRunner.Spawn(player, inputAuthority: peer);
```

## Comparison

| Feature | Nebula | Godot Built-in | Netfox |
|---------|--------|----------------|--------|
| **Language** | C# only | GDScript & C# | GDScript & C# |
| **Architecture** | Server-authoritative | Flexible (RPC-based) | Server-authoritative |
| **Client-side Prediction** | ✅ Built-in | ❌ Manual | ✅ Built-in |
| **Rollback/Reconciliation** | ✅ Automatic | ❌ Manual | ✅ Automatic |
| **Interpolation** | ✅ Per-property | ✅ MultiplayerSynchronizer | ✅ Built-in |
| **Interest Management** | ✅ Fine-grained (property-level) | ⚠️ Basic (visibility) | ❌ Not built-in |
| **Multiple Game Worlds** | ✅ Built-in | ❌ Manual | ❌ Not built-in |
| **Tick-based Simulation** | ✅ Yes | ❌ Frame-based | ✅ Yes |
| **Source Generation** | ✅ Zero boilerplate | ❌ N/A | ❌ N/A |
| **Compile-time Validation** | ✅ Yes | ❌ No | ❌ No |
| **Transport** | ENet (UDP) | ENet, WebSocket, WebRTC | Godot's built-in |
| **Learning Curve** | Moderate | Low | Moderate |

### When to Choose Each

**Choose Nebula if you:**
- Are building in C#, want maximum type safety, and performance optimization
- Need fine-grained control over what data each player sees
- Want prediction and interpolation to "just work" together
- Are building a game with lobbies/matches (multiple worlds)

**Choose Godot's built-in multiplayer if you:**
- Want the simplest possible setup
- Are building a casual/turn-based game where latency isn't critical
- Prefer GDScript and minimal dependencies

**Choose Netfox if you:**
- Prefer GDScript but still want prediction/rollback
- Want the noray integration for easier connectivity
- Are looking for a middle-ground solution

## Documentation

Comprehensive documentation is available at: **https://nebula.dev.heavenlode.com**

For implementation details and architecture overview, see [NEBULA_OVERVIEW.mdc](./NEBULA_OVERVIEW.mdc). This file is particularly useful for AI/vibe coding as well.

## Roadmap

- [x] Client-side prediction and rollback
- [x] Property interpolation
- [x] Interest management
- [x] Multiple world support
- [ ] Physics rollback (blocked by [Godot #2821](https://github.com/godotengine/godot-proposals/issues/2821))
- [ ] Lag compensation for projectiles/hitscan

## Community

- **[Discord](https://discord.gg/AUjzVA4sEK)** — Chat, questions, and support
- **[GitHub Issues](https://github.com/Heavenlode/Nebula/issues)** — Bug reports and feature requests

## Contributing

Contributions are welcome! Whether it's bug reports, feature requests, documentation improvements, or code contributions.

1. **Questions or ideas?** Drop by the [Discord](https://discord.gg/AUjzVA4sEK) or [open an issue](https://github.com/Heavenlode/Nebula/issues/new)
2. **Want to contribute code?** Please open an issue first to discuss your proposal
3. **Found a bug?** Bug reports with reproduction steps are incredibly helpful

---

<p align="center">
  <i>Built with 💜 for the Godot community</i>
</p>
