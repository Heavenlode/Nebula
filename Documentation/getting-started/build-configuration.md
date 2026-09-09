# Build Configuration

Nebula compiles two things differently per build. Both are decided by `Nebula.props` and, for exports,
set per preset by Nebula's export plugin. The editor build is never affected.

## Network role

`NetRunner.IsServer` and `NetRunner.IsClient` are static. In an exported build they are `const`, so
`if (NetRunner.IsClient) return;` and every branch like it is removed by the compiler and the other
role's code is never emitted. In the editor they are decided at runtime by `StartServer()`.

| Where | Value |
|---|---|
| Editor (Debug) | runtime |
| Export | preset option `nebula/role`: **Auto** (default: server if the preset is a dedicated server, else client), Client, Server, Runtime |
| Manual `dotnet publish` | `-p:NebulaRole=server\|client\|runtime` (default runtime) |

A role-fixed build launched as the other role exits at startup. Read the role only through
`NetRunner.IsServer` / `NetRunner.IsClient`; there is no instance form. Tests that need the server
path in the editor call `NetRunner.ForceRoleForTests(true)`.

## BSON support

MongoDB.Bson and the BSON persistence API (`IBsonSerializable`, `BsonSerialize` / `BsonDeserialize`,
`BsonTypeHelper`, generated `WriteBsonProperties` / `ReadBsonProperties`) compile only when enabled.
Off by default.

| Where | Value |
|---|---|
| Editor (Debug) | project setting `Nebula/config/build/bson_support` |
| Export | preset option `nebula/bson_support` |
| Manual `dotnet publish` | `-p:NebulaBsonSupport=true\|false` |

Game code that persists goes under `#if NEBULA_BSON_SUPPORT`; a build without it fails to compile on
any use, which is intended.

## Trimming (NativeAOT platforms)

Godot's SDK roots the whole game assembly, so nothing in it is trimmed. `NebulaTrimGame=true` (csproj
property or `-p:NebulaTrimGame=true`) removes that root; the trimmer keeps only what Godot's script
registration, scenes and generated dispatch reach. Default: false.

- Applies where `PublishAot` is true (iOS). No effect elsewhere.
- Code reached only by name (reflection, GDScript `load("X.cs").new()`) must be listed in an
  `ILLink.Descriptors.xml` embedded in the game project, or it is removed.
- Verify on a device. A class whose trim warnings disappear after enabling this was removed, not fixed.
- Exports never compile `addons/Nebula/Testing/**` or xunit. Exclude your own tests the same way:
  `<Compile Remove="**\*Tests.cs" Condition="'$(Configuration)' != 'Debug'" />`.
