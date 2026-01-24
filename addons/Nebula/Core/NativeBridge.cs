using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot;

namespace Nebula;

/// <summary>
/// Zero-allocation bridge to Godot node properties via native P/Invoke.
/// 
/// Usage:
/// 1. Register nodes in _Ready: NativeBridge.Register(this);
/// 2. Call SyncFromGodot() once per frame/tick to refresh caches
/// 3. Use GetGlobalPosition(), GetLinearVelocity(), etc. instead of Godot properties
/// 4. Call FlushToGodot() to apply pending writes (SetVisible, SetProcessMode)
/// 5. Unregister in cleanup: NativeBridge.Unregister(this);
/// </summary>
public static class NativeBridge
{
    private const string LibName = "nebula_bridge";
    
    private static bool _initialized = false;
    private static bool _available = false;
    
    static NativeBridge()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeBridge).Assembly, ResolveDll);
    }
    
    private static IntPtr ResolveDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibName) return IntPtr.Zero;
        
        // Build list of search paths - executable directory first (for exports), then addon path (for editor)
        var searchPaths = new[]
        {
            Path.GetDirectoryName(OS.GetExecutablePath()),
            ProjectSettings.GlobalizePath("res://addons/NebulaBridge")
        };
        
        // Build list of library names to try per platform
        string[] libNames;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            libNames = new[]
            {
                "libnebula_bridge.linux.template_release.x86_64.so",
                "libnebula_bridge.linux.template_debug.x86_64.so"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            libNames = new[]
            {
                "libnebula_bridge.macos.template_debug.framework/libnebula_bridge.macos.template_debug",
                "libnebula_bridge.macos.template_release.framework/libnebula_bridge.macos.template_release"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            libNames = new[]
            {
                "nebula_bridge.windows.template_release.x86_64.dll",
                "nebula_bridge.windows.template_debug.x86_64.dll"
            };
        }
        else
        {
            return IntPtr.Zero;
        }
        
        // Try each search path with each library name
        foreach (var basePath in searchPaths)
        {
            foreach (var libName in libNames)
            {
                var libPath = Path.Combine(basePath, libName);
                if (File.Exists(libPath))
                {
                    GD.Print($"[NativeBridge] Loading library from: {libPath}");
                    if (NativeLibrary.TryLoad(libPath, out var handle))
                        return handle;
                }
            }
        }
        
        GD.PrintErr($"[NativeBridge] Library not found in any search path");
        return IntPtr.Zero;
    }
    
    // Check if the native library is available
    public static bool IsAvailable
    {
        get
        {
            if (!_initialized)
            {
                _initialized = true;
                try
                {
                    // Try a simple call to check if library is loaded
                    bridge_sync_from_godot();
                    _available = true;
                }
                catch (DllNotFoundException)
                {
                    _available = false;
                    GD.PrintErr("[NativeBridge] Native library not found. Falling back to Godot API.");
                }
                catch (Exception e)
                {
                    _available = false;
                    GD.PrintErr($"[NativeBridge] Error initializing: {e.Message}");
                }
            }
            return _available;
        }
    }
    
    #region DllImport Declarations
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_register_node(ulong instanceId, IntPtr nodePtr);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_unregister_node(ulong instanceId);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_sync_from_godot();
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_flush_to_godot();
    
    // Node3D
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_position(ulong id, out float x, out float y, out float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_global_position(ulong id, out float x, out float y, out float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_rotation(ulong id, out float x, out float y, out float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_global_transform(ulong id,
        out float ox, out float oy, out float oz,
        out float xx, out float xy, out float xz,
        out float yx, out float yy, out float yz,
        out float zx, out float zy, out float zz);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_basis(ulong id,
        out float xx, out float xy, out float xz,
        out float yx, out float yy, out float yz,
        out float zx, out float zy, out float zz);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_local_basis(ulong id,
        out float xx, out float xy, out float xz,
        out float yx, out float yy, out float yz,
        out float zx, out float zy, out float zz);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool bridge_get_visible(ulong id);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int bridge_get_process_mode(ulong id);
    
    // RigidBody3D
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_linear_velocity(ulong id, out float x, out float y, out float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_angular_velocity(ulong id, out float x, out float y, out float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern float bridge_get_mass(ulong id);
    
    // CharacterBody3D
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_get_velocity(ulong id, out float x, out float y, out float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool bridge_is_on_floor(ulong id);
    
    // RayCast3D
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool bridge_raycast_is_colliding(ulong id);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_raycast_get_collision_point(ulong id, out float x, out float y, out float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_raycast_get_collision_normal(ulong id, out float x, out float y, out float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong bridge_raycast_get_collider_id(ulong id);
    
    // Setters
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_set_visible(ulong id, [MarshalAs(UnmanagedType.I1)] bool visible);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_set_process_mode(ulong id, int mode);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_set_position(ulong id, float x, float y, float z);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_set_basis(ulong id,
        float xx, float xy, float xz,
        float yx, float yy, float yz,
        float zx, float zy, float zz);
    
    // Utility
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern float bridge_distance_to(ulong idA, ulong idB);
    
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern float bridge_distance_squared_to(ulong idA, ulong idB);
    
    #endregion
    
    #region Registration
    
    /// <summary>
    /// Register a node for cached property access.
    /// Call this in _Ready() for nodes you want to access via the bridge.
    /// </summary>
    public static void Register(Node node)
    {
        if (!IsAvailable) return;
        bridge_register_node(node.GetInstanceId(), node.NativeInstance);
    }
    
    /// <summary>
    /// Unregister a node. Call this when the node is being freed.
    /// </summary>
    public static void Unregister(Node node)
    {
        if (!IsAvailable) return;
        bridge_unregister_node(node.GetInstanceId());
    }
    
    #endregion
    
    #region Sync
    
    /// <summary>
    /// Refresh all cached node properties from Godot.
    /// Call this once per frame/tick before reading cached values.
    /// </summary>
    public static void SyncFromGodot()
    {
        if (!IsAvailable) return;
        bridge_sync_from_godot();
    }
    
    /// <summary>
    /// Apply all pending property writes to Godot.
    /// Call this once per frame/tick after setting values.
    /// </summary>
    public static void FlushToGodot()
    {
        if (!IsAvailable) return;
        bridge_flush_to_godot();
    }
    
    #endregion
    
    #region Node3D Getters
    
    /// <summary>
    /// Get cached local position. Zero allocations.
    /// </summary>
    public static Vector3 GetPosition(Node3D node)
    {
        if (!IsAvailable) return node.Position;
        bridge_get_position(node.GetInstanceId(), out var x, out var y, out var z);
        return new Vector3(x, y, z);
    }
    
    /// <summary>
    /// Get cached global position. Zero allocations.
    /// </summary>
    public static Vector3 GetGlobalPosition(Node3D node)
    {
        if (!IsAvailable) return node.GlobalPosition;
        bridge_get_global_position(node.GetInstanceId(), out var x, out var y, out var z);
        return new Vector3(x, y, z);
    }
    
    /// <summary>
    /// Get cached rotation (euler angles). Zero allocations.
    /// </summary>
    public static Vector3 GetRotation(Node3D node)
    {
        if (!IsAvailable) return node.Rotation;
        bridge_get_rotation(node.GetInstanceId(), out var x, out var y, out var z);
        return new Vector3(x, y, z);
    }
    
    /// <summary>
    /// Get cached global transform. Zero allocations.
    /// </summary>
    public static Transform3D GetGlobalTransform(Node3D node)
    {
        if (!IsAvailable) return node.GlobalTransform;
        bridge_get_global_transform(node.GetInstanceId(),
            out var ox, out var oy, out var oz,
            out var xx, out var xy, out var xz,
            out var yx, out var yy, out var yz,
            out var zx, out var zy, out var zz);
        
        return new Transform3D(
            new Basis(
                new Vector3(xx, xy, xz),
                new Vector3(yx, yy, yz),
                new Vector3(zx, zy, zz)
            ),
            new Vector3(ox, oy, oz)
        );
    }
    
    /// <summary>
    /// Get cached global transform basis. Zero allocations.
    /// </summary>
    public static Basis GetGlobalBasis(Node3D node)
    {
        if (!IsAvailable) return node.GlobalTransform.Basis;
        bridge_get_basis(node.GetInstanceId(),
            out var xx, out var xy, out var xz,
            out var yx, out var yy, out var yz,
            out var zx, out var zy, out var zz);
        
        return new Basis(
            new Vector3(xx, xy, xz),
            new Vector3(yx, yy, yz),
            new Vector3(zx, zy, zz)
        );
    }
    
    /// <summary>
    /// Get cached local basis. Zero allocations.
    /// Returns fresh value even after SetBasis within same tick.
    /// </summary>
    public static Basis GetBasis(Node3D node)
    {
        if (!IsAvailable) return node.Transform.Basis;
        bridge_get_local_basis(node.GetInstanceId(),
            out var xx, out var xy, out var xz,
            out var yx, out var yy, out var yz,
            out var zx, out var zy, out var zz);
        
        return new Basis(
            new Vector3(xx, xy, xz),
            new Vector3(yx, yy, yz),
            new Vector3(zx, zy, zz)
        );
    }
    
    /// <summary>
    /// Get cached visibility. Zero allocations.
    /// </summary>
    public static bool GetVisible(Node3D node)
    {
        if (!IsAvailable) return node.Visible;
        return bridge_get_visible(node.GetInstanceId());
    }
    
    /// <summary>
    /// Get cached process mode. Zero allocations.
    /// </summary>
    public static Node.ProcessModeEnum GetProcessMode(Node node)
    {
        if (!IsAvailable) return node.ProcessMode;
        return (Node.ProcessModeEnum)bridge_get_process_mode(node.GetInstanceId());
    }
    
    #endregion
    
    #region RigidBody3D Getters
    
    /// <summary>
    /// Get cached linear velocity. Zero allocations.
    /// </summary>
    public static Vector3 GetLinearVelocity(RigidBody3D node)
    {
        if (!IsAvailable) return node.LinearVelocity;
        bridge_get_linear_velocity(node.GetInstanceId(), out var x, out var y, out var z);
        return new Vector3(x, y, z);
    }
    
    /// <summary>
    /// Get cached angular velocity. Zero allocations.
    /// </summary>
    public static Vector3 GetAngularVelocity(RigidBody3D node)
    {
        if (!IsAvailable) return node.AngularVelocity;
        bridge_get_angular_velocity(node.GetInstanceId(), out var x, out var y, out var z);
        return new Vector3(x, y, z);
    }
    
    /// <summary>
    /// Get cached mass. Zero allocations.
    /// </summary>
    public static float GetMass(RigidBody3D node)
    {
        if (!IsAvailable) return node.Mass;
        return bridge_get_mass(node.GetInstanceId());
    }
    
    #endregion
    
    #region CharacterBody3D Getters
    
    /// <summary>
    /// Get cached velocity. Zero allocations.
    /// </summary>
    public static Vector3 GetVelocity(CharacterBody3D node)
    {
        if (!IsAvailable) return node.Velocity;
        bridge_get_velocity(node.GetInstanceId(), out var x, out var y, out var z);
        return new Vector3(x, y, z);
    }
    
    /// <summary>
    /// Get cached is_on_floor. Zero allocations.
    /// </summary>
    public static bool IsOnFloor(CharacterBody3D node)
    {
        if (!IsAvailable) return node.IsOnFloor();
        return bridge_is_on_floor(node.GetInstanceId());
    }
    
    #endregion
    
    #region RayCast3D Getters
    
    /// <summary>
    /// Get cached is_colliding. Zero allocations.
    /// </summary>
    public static bool IsColliding(RayCast3D ray)
    {
        if (!IsAvailable) return ray.IsColliding();
        return bridge_raycast_is_colliding(ray.GetInstanceId());
    }
    
    /// <summary>
    /// Get cached collision point. Zero allocations.
    /// </summary>
    public static Vector3 GetCollisionPoint(RayCast3D ray)
    {
        if (!IsAvailable) return ray.GetCollisionPoint();
        bridge_raycast_get_collision_point(ray.GetInstanceId(), out var x, out var y, out var z);
        return new Vector3(x, y, z);
    }
    
    /// <summary>
    /// Get cached collision normal. Zero allocations.
    /// </summary>
    public static Vector3 GetCollisionNormal(RayCast3D ray)
    {
        if (!IsAvailable) return ray.GetCollisionNormal();
        bridge_raycast_get_collision_normal(ray.GetInstanceId(), out var x, out var y, out var z);
        return new Vector3(x, y, z);
    }
    
    /// <summary>
    /// Get cached collider instance ID. Zero allocations.
    /// </summary>
    public static ulong GetColliderId(RayCast3D ray)
    {
        if (!IsAvailable)
        {
            var collider = ray.GetCollider();
            return collider?.GetInstanceId() ?? 0;
        }
        return bridge_raycast_get_collider_id(ray.GetInstanceId());
    }
    
    #endregion
    
    #region Setters (Deferred)
    
    /// <summary>
    /// Set visibility (deferred until FlushToGodot).
    /// </summary>
    public static void SetVisible(Node3D node, bool visible)
    {
        if (!IsAvailable)
        {
            node.Visible = visible;
            return;
        }
        bridge_set_visible(node.GetInstanceId(), visible);
    }
    
    /// <summary>
    /// Set process mode (deferred until FlushToGodot).
    /// </summary>
    public static void SetProcessMode(Node node, Node.ProcessModeEnum mode)
    {
        if (!IsAvailable)
        {
            node.ProcessMode = mode;
            return;
        }
        bridge_set_process_mode(node.GetInstanceId(), (int)mode);
    }
    
    /// <summary>
    /// Set position (updates cache immediately and defers Godot write until FlushToGodot).
    /// This ensures subsequent reads via GetPosition return the updated value.
    /// </summary>
    public static void SetPosition(Node3D node, Vector3 position)
    {
        if (!IsAvailable)
        {
            node.Position = position;
            return;
        }
        bridge_set_position(node.GetInstanceId(), position.X, position.Y, position.Z);
    }
    
    /// <summary>
    /// Set basis/rotation (updates cache immediately and defers Godot write until FlushToGodot).
    /// This ensures subsequent reads via GetBasis return the updated value.
    /// </summary>
    public static void SetBasis(Node3D node, Basis basis)
    {
        if (!IsAvailable)
        {
            var t = node.Transform;
            t.Basis = basis;
            node.Transform = t;
            return;
        }
        // Pass columns to match C++ expectation
        var col0 = basis.Column0;
        var col1 = basis.Column1;
        var col2 = basis.Column2;
        bridge_set_basis(node.GetInstanceId(),
            col0.X, col0.Y, col0.Z,
            col1.X, col1.Y, col1.Z,
            col2.X, col2.Y, col2.Z);
    }
    
    #endregion
    
    #region Utility
    
    /// <summary>
    /// Calculate distance between two registered nodes. Zero allocations.
    /// </summary>
    public static float DistanceTo(Node3D nodeA, Node3D nodeB)
    {
        if (!IsAvailable) return nodeA.GlobalPosition.DistanceTo(nodeB.GlobalPosition);
        return bridge_distance_to(nodeA.GetInstanceId(), nodeB.GetInstanceId());
    }
    
    /// <summary>
    /// Calculate squared distance between two registered nodes. Zero allocations.
    /// Faster than DistanceTo when you only need to compare distances.
    /// </summary>
    public static float DistanceSquaredTo(Node3D nodeA, Node3D nodeB)
    {
        if (!IsAvailable) return nodeA.GlobalPosition.DistanceSquaredTo(nodeB.GlobalPosition);
        return bridge_distance_squared_to(nodeA.GetInstanceId(), nodeB.GetInstanceId());
    }
    
    #endregion
}
