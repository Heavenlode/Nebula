# NetNode3D Class


Node3D, extended with Nebula networking capabilities. This is the most basic networked 3D object. See <a href="T_Nebula_NetNode">NetNode</a> for more information.



## Definition
**Namespace:** <a href="N_Nebula">Nebula</a>  
**Assembly:** Nebula (in Nebula.dll) Version: 1.0.0+a74e1f454fb572dfd95c249b7895aa6542c85b05

**C#**
``` C#
public class NetNode3D : Node3D, INetNode, 
	INotifyPropertyChanged, INetSerializable<NetNode3D>, IBsonSerializable<NetNode3D>, 
	IBsonSerializableBase
```

<table><tr><td><strong>Inheritance</strong></td><td><a href="https://learn.microsoft.com/dotnet/api/system.object" target="_blank" rel="noopener noreferrer">Object</a>  →  GodotObject  →  Node  →  Node3D  →  NetNode3D</td></tr>
<tr><td><strong>Derived</strong></td><td><a href="T_Nebula_Utility_Nodes_NetTransform3D">Nebula.Utility.Nodes.NetTransform3D</a></td></tr>
<tr><td><strong>Implements</strong></td><td><a href="T_Nebula_INetNode">INetNode</a>, <a href="T_Nebula_Serialization_IBsonSerializable_1">IBsonSerializable</a>(NetNode3D), <a href="T_Nebula_Serialization_IBsonSerializableBase">IBsonSerializableBase</a>, <a href="T_Nebula_Serialization_INetSerializable_1">INetSerializable</a>(NetNode3D), <a href="https://learn.microsoft.com/dotnet/api/system.componentmodel.inotifypropertychanged" target="_blank" rel="noopener noreferrer">INotifyPropertyChanged</a></td></tr>
</table>



## Constructors
<table>
<tr>
<td><a href="M_Nebula_NetNode3D__ctor">NetNode3D</a></td>
<td>Initializes a new instance of the NetNode3D class</td></tr>
</table>

## Properties
<table>
<tr>
<td><a href="P_Nebula_NetNode3D_Network">Network</a></td>
<td> </td></tr>
<tr>
<td><a href="P_Nebula_NetNode3D_Serializers">Serializers</a></td>
<td> </td></tr>
</table>

## Methods
<table>
<tr>
<td><a href="M_Nebula_NetNode3D__NetworkProcess">_NetworkProcess</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D__PhysicsProcess">_PhysicsProcess</a></td>
<td><p>Called during the physics processing step of the main loop. Physics processing means that the frame rate is synced to the physics, i.e. the <em>delta</em> parameter will <em>generally</em> be constant (see exceptions below). <em>delta</em> is in seconds.</p><p>

It is only called if physics processing is enabled, which is done automatically if this method is overridden, and can be toggled with SetPhysicsProcess(Boolean).</p><p>

Processing happens in order of ProcessPhysicsPriority, lower priority values are called first. Nodes with the same priority are processed in tree order, or top to bottom as seen in the editor (also known as pre-order traversal).</p><p>

Corresponds to the NotificationPhysicsProcess notification in _Notification(Int32).</p><p><b>

Note:</b> This method is only called if the node is present in the scene tree (i.e. if it's not an orphan).</p><p><b>

Note:</b><em>delta</em> will be larger than expected if running at a framerate lower than PhysicsTicksPerSecond / MaxPhysicsStepsPerFrame FPS. This is done to avoid "spiral of death" scenarios where performance would plummet due to an ever-increasing number of physics steps per frame. This behavior affects both _Process(Double) and _PhysicsProcess(Double). As a result, avoid using <em>delta</em> for time measurements in real-world seconds. Use the Time singleton's methods for this purpose instead, such as GetTicksUsec().</p><br />(Overrides Node._PhysicsProcess(Double))</td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D__WorldReady">_WorldReady</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D_BsonDeserialize">BsonDeserialize</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D_BsonSerialize">BsonSerialize</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D_NetworkDeserialize">NetworkDeserialize</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D_NetworkSerialize">NetworkSerialize</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D_NodePathFromNetScene">NodePathFromNetScene</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D_OnPropertyChanged">OnPropertyChanged(PropertyChangedEventArgs)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D_OnPropertyChanged_1">OnPropertyChanged(String)</a></td>
<td> </td></tr>
<tr>
<td><a href="M_Nebula_NetNode3D_SetupSerializers">SetupSerializers</a></td>
<td> </td></tr>
</table>

## Events
<table>
<tr>
<td><a href="E_Nebula_NetNode3D_PropertyChanged">PropertyChanged</a></td>
<td> </td></tr>
</table>

## See Also


#### Reference
<a href="N_Nebula">Nebula Namespace</a>  
