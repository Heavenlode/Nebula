# LookupProperty Method


Lookup a property by its scene, node, and name.



## Definition
**Namespace:** <a href="N_Nebula_Serialization">Nebula.Serialization</a>  
**Assembly:** Nebula (in Nebula.dll) Version: 1.0.0+a74e1f454fb572dfd95c249b7895aa6542c85b05

**C#**
``` C#
public bool LookupProperty(
	string scene,
	string node,
	string property,
	out ProtocolNetProperty prop
)
```



#### Parameters
<dl><dt>  <a href="https://learn.microsoft.com/dotnet/api/system.string" target="_blank" rel="noopener noreferrer">String</a></dt><dd>The scene path.</dd><dt>  <a href="https://learn.microsoft.com/dotnet/api/system.string" target="_blank" rel="noopener noreferrer">String</a></dt><dd>The node path.</dd><dt>  <a href="https://learn.microsoft.com/dotnet/api/system.string" target="_blank" rel="noopener noreferrer">String</a></dt><dd>The property name.</dd><dt>  <a href="T_Nebula_Serialization_ProtocolNetProperty">ProtocolNetProperty</a></dt><dd>The property, if found.</dd></dl>

#### Return Value
<a href="https://learn.microsoft.com/dotnet/api/system.boolean" target="_blank" rel="noopener noreferrer">Boolean</a>  
True if the property was found, false otherwise.

## See Also


#### Reference
<a href="T_Nebula_Serialization_ProtocolRegistry">ProtocolRegistry Class</a>  
<a href="N_Nebula_Serialization">Nebula.Serialization Namespace</a>  
