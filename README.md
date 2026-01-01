# Nebula

Godot C# Netcode from another galaxy 🌌

## Table of contents
- [Nebula](#Nebula)
  - [Table of contents](#table-of-contents)
  - [Sample](#sample)
  - [Features](#features)
    - [Coming soon](#coming-soon)
  - [Getting started](#getting-started)
    - [Pre-setup notes](#pre-setup-notes)
    - [Setup steps](#setup-steps)
      - [Help](#help)
  - [Examples](#examples)
  - [Documentation](#documentation)
  - [What is this?](#what-is-this)
  - [How to contribute](#how-to-contribute)

## Sample

```cs
using Godot;
using Nebula;

namespace MyGame
{
	public partial class PlayableCharacter : NetNode3D
	{
		[NetProperty]
		public int Money { get; set; }

		public OnNetworkChangeMoney(Tick tick, int oldVal, int newVal)
		{
			// This is only called on the client-side
			GD.Print("I have " + newVal + " money now! Thanks, server.");
		}

		public override void _NetworkProcess(int _tick)
		{
			base._NetworkProcess(_tick);
			if (NetRunner.Instance.IsServer)
			{
				// This is only called on the server-side
				Money += 1;
			}
		}
	}
}
```

## Features

* Tick-aligned system
* Single state authority (Dedicated-server model)
* Input authority (cheat prevention)
* Data Interpolation
* Advanced, variable-level Interest management
* Multiple "Worlds" within one Godot server instance

### Coming soon
* Rollback / Lag compensation (blocked by [Godot #2821](https://github.com/godotengine/godot-proposals/issues/2821))

## Getting started

### Pre-setup notes

* Requires Godot.NET (this is a C# library)

### Setup steps

1. Add Nebula to your "addons"
2. Import Nebula in your `.csproj`. For example:
```xml
<Project Sdk="Godot.NET.Sdk/4.5.1">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <Import Project="addons\Nebula\Nebula.props" />
</Project>
```
3. Enable the Nebula plugin in Godot

#### Help
Better documentation and demo projects are coming soon. In the meantime, feel free to [open a ticket](https://github.com/Heavenlode/Nebula/issues/new) with any questions or concerns.

## Examples
Coming soon!

## Documentation

More comprehensive documentation is available here: https://nebula.dev.heavenlode.com

## What is this?

Nebula is a library for developing online multiplayer games with Godot. It is a tick-aligned system, respects input authority, and all state is managed by the server.

This library is still in BETA, therefore it is not quite production-ready and is subject to changes as we approach stability.

## How to contribute

If you have questions, comments, or concerns, please feel free to [open a ticket](https://github.com/Heavenlode/Nebula/issues/new).

If you want to contribute code and/or documentation, you're amazing! To maximize your odds of your feature/PR getting merged in, you are encouraged to first [open a ticket](https://github.com/Heavenlode/Nebula/issues/new) with your proposal. Once approved, any and all work is very much appreciated.
