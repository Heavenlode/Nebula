# WorldRunner Class


Manages the network state of all <a href="T_Nebula_NetNode">NetNode</a>s in the scene. Inside the <a href="T_Nebula_NetRunner">NetRunner</a> are one or more “Worlds”. Each World represents some part of the game that is isolated from other parts. For example, different maps, dungeon instances, etc. Worlds are dynamically created by calling [!:NetRunner.CreateWorld]. Worlds cannot directly interact with each other and do not share state. Players only exist in one World at a time, so it can be helpful to think of the clients as being connected to a World directly.



## Definition
**Namespace:** <a href="N_Nebula">Nebula</a>  
**Assembly:** Nebula (in Nebula.dll) Version: 1.0.0+a74e1f454fb572dfd95c249b7895aa6542c85b05

**C#**
``` C#
public class WorldRunner : Node
```

<table><tr><td><strong>Inheritance</strong></td><td><a href="https://learn.microsoft.com/dotnet/api/system.object" target="_blank" rel="noopener noreferrer">Object</a>  →  GodotObject  →  Node  →  WorldRunner</td></tr>
</table>



## Constructors
<table>
<tr>
<td><a href="M_Nebula_WorldRunner__ctor">WorldRunner</a></td>
<td>Initializes a new instance of the WorldRunner class</td></tr>
</table>

## Properties
<table>
<tr>
<td><a href="P_Nebula_WorldRunner_CurrentTick">CurrentTick</a></td>
<td>The current network tick. On the client side, this does not represent the server's current tick, which will always be slightly ahead.</td></tr>
<tr>
<td><a href="P_Nebula_WorldRunner_CurrentWorld">CurrentWorld</a></td>
<td>Only applicable on the client side.</td></tr>
<tr>
<td><a href="P_Nebula_WorldRunner_DebugEnet">DebugEnet</a></td>
<td> </td></tr>
<tr>
<td><a href="P_Nebula_WorldRunner_InputStore">InputStore</a></td>
<td> </td></tr>
<tr>
<td><a href="P_Nebula_WorldRunner_WorldId">WorldId</a></td>
<td> </td></tr>
</table>

## Methods
<table>
<tr>
<td><a href="M_Nebula_WorldRunner__ExitTree">_ExitTree</a></td>
<td><br />(Overrides Node._ExitTree())</td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner__PhysicsProcess">_PhysicsProcess</a></td>
<td><br />(Overrides Node._PhysicsProcess(Double))</td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner__Ready">_Ready</a></td>
<td><br />(Overrides Node._Ready())</td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_AllocateNetId">AllocateNetId()</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_AllocateNetId_1">AllocateNetId(Byte)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_ChangeScene">ChangeScene</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_CheckStaticInitialization">CheckStaticInitialization</a></td>
<td>This is called for nodes that are initialized in a scene by default. Clients automatically dequeue all network nodes on initialization. All network nodes on the client side must come from the server by gaining Interest in the node.</td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_ClientHandleTick">ClientHandleTick</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_EmitSignalOnAfterNetworkTick">EmitSignalOnAfterNetworkTick</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_EmitSignalOnPeerSyncStatusChange">EmitSignalOnPeerSyncStatusChange</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_EmitSignalOnPlayerJoined">EmitSignalOnPlayerJoined</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_GetNetId">GetNetId</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_GetNetIdFromPeerId">GetNetIdFromPeerId</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_GetNodeFromNetId_1">GetNodeFromNetId(Int64)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_GetNodeFromNetId">GetNodeFromNetId(NetId)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_GetPeerNode">GetPeerNode</a></td>
<td>Get the network node from a peer and a network ID relative to that peer.</td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_GetPeerNodeId">GetPeerNodeId</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_GetPeerWorldState">GetPeerWorldState(ENetPacketPeer)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_GetPeerWorldState_1">GetPeerWorldState(UUID)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_HasSpawnedForClient">HasSpawnedForClient</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_Log">Log</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_PeerAcknowledge">PeerAcknowledge</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_QueuePeerState">QueuePeerState</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_ServerProcessTick">ServerProcessTick</a></td>
<td>This method is executed every tick on the Server side, and kicks off all logic which processes and sends data to every client.</td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_SetPeerState">SetPeerState(ENetPacketPeer, WorldRunner.PeerState)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_SetPeerState_1">SetPeerState(UUID, WorldRunner.PeerState)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_SetSpawnedForClient">SetSpawnedForClient</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_WorldRunner_Spawn__1">Spawn(T)</a></td>
<td> </td></tr>
</table>

## Events
<table>
<tr>
<td><a href="E_Nebula_WorldRunner_OnAfterNetworkTick">OnAfterNetworkTick</a></td>
<td> </td></tr>
<tr>
<td><a href="E_Nebula_WorldRunner_OnPeerSyncStatusChange">OnPeerSyncStatusChange</a></td>
<td> </td></tr>
<tr>
<td><a href="E_Nebula_WorldRunner_OnPlayerJoined">OnPlayerJoined</a></td>
<td> </td></tr>
</table>

## Fields
<table>
<tr>
<td><a href="F_Nebula_WorldRunner_ClientAvailableNodes">ClientAvailableNodes</a></td>
<td> </td></tr>
<tr>
<td><a href="F_Nebula_WorldRunner_RootScene">RootScene</a></td>
<td>Only used by the client to determine the current root scene.</td></tr>
</table>

## See Also


#### Reference
<a href="N_Nebula">Nebula Namespace</a>  
