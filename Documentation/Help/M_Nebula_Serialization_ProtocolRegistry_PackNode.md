# PackNode Method


Pack a scene's NetNode by path into a byte to be sent over the network.



## Definition
**Namespace:** <a href="N_Nebula_Serialization">Nebula.Serialization</a>  
**Assembly:** Nebula (in Nebula.dll) Version: 1.0.0+a74e1f454fb572dfd95c249b7895aa6542c85b05

**C#**
``` C#
public bool PackNode(
	string scene,
	string node,
	out byte nodeId
)
```



#### Parameters
<dl><dt>  <a href="https://learn.microsoft.com/dotnet/api/system.string" target="_blank" rel="noopener noreferrer">String</a></dt><dd>The scene path.</dd><dt>  <a href="https://learn.microsoft.com/dotnet/api/system.string" target="_blank" rel="noopener noreferrer">String</a></dt><dd>The node path.</dd><dt>  <a href="https://learn.microsoft.com/dotnet/api/system.byte" target="_blank" rel="noopener noreferrer">Byte</a></dt><dd>The node byte.</dd></dl>

#### Return Value
<a href="https://learn.microsoft.com/dotnet/api/system.boolean" target="_blank" rel="noopener noreferrer">Boolean</a>  
True if the node was found, false otherwise.

## See Also


#### Reference
<a href="T_Nebula_Serialization_ProtocolRegistry">ProtocolRegistry Class</a>  
<a href="N_Nebula_Serialization">Nebula.Serialization Namespace</a>  
