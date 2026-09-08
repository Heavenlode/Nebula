#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nebula;

/// <summary>
/// Resolves every <c>[DllImport]</c> in this assembly against native libraries vendored as build
/// output and copied next to the assembly: ENet from <c>addons/Nebula/native</c>, and whatever
/// else a game project copies beside it the same way (Heavenlode's Steam API library, for one).
/// </summary>
/// <remarks>
/// Godot loads game assemblies into a load context whose <c>AssemblyDependencyResolver</c>
/// resolves native libraries from the deps.json file alone, and does not probe beside the
/// assembly. NuGet packages list their natives there as RID-specific assets; vendored copies are
/// plain build output and are not, whichever directory layout they are copied into. Without this
/// resolver every such P/Invoke fails with <c>DllNotFoundException</c> under Godot.
///
/// This is deliberately ONE resolver with a naming convention rather than one per library:
/// .NET accepts a single DllImport resolver per assembly, and a second
/// <see cref="NativeLibrary.SetDllImportResolver"/> for the same assembly throws
/// <see cref="InvalidOperationException"/>. Nebula compiles into the game assembly, so any library
/// the game vendors shares this one. The convention is the platform's usual file name for the
/// import name, with and without the <c>lib</c> prefix, which covers <c>enet</c> (enet.dll /
/// libenet.dylib / libenet.so) and <c>steam_api</c> / <c>steam_api64</c> alike.
/// </remarks>
internal static class VendoredNativeResolver
{
    private static bool _installed;

    // CA2255 warns off module initializers in libraries. Nebula is compiled into the game
    // assembly rather than shipped as one, and registering here is what guarantees the resolver
    // is in place before any P/Invoke, not just the ones on the NetRunner startup path.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(VendoredNativeResolver).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for this assembly, so there is nothing to add.
            // Swallowed deliberately: this runs as a module initializer, where an escaping
            // exception fails the load of the entire assembly instead of just this lookup. The
            // worst case without it is the DllNotFoundException we would have had anyway, raised
            // at the call site where it is far easier to read.
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // "__Internal" (statically linked, iOS) and explicit paths are the runtime's business.
        if (libraryName.StartsWith("__", StringComparison.Ordinal) ||
            libraryName.Contains('/') || libraryName.Contains('\\'))
        {
            return IntPtr.Zero;
        }

        foreach (var directory in new[] { Path.GetDirectoryName(assembly.Location), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            foreach (var fileName in CandidateFileNames(libraryName))
            {
                var path = Path.Combine(directory, fileName);

                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The file names an import name may have been copied to, per platform. The bare name first
    /// where that is the platform's convention (Windows), the <c>lib</c>-prefixed one first
    /// elsewhere; both are always tried so a Link name in a .props/.csproj cannot silently miss.
    /// </summary>
    private static string[] CandidateFileNames(string libraryName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? new[] { libraryName }
                : new[] { libraryName + ".dll", "lib" + libraryName + ".dll" };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new[] { "lib" + libraryName + ".dylib", libraryName + ".dylib" };
        }

        return new[] { "lib" + libraryName + ".so", libraryName + ".so" };
    }
}
