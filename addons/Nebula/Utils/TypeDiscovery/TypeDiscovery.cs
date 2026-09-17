using System;
using System.Collections.Generic;
using System.Reflection;

namespace Nebula.Utility.Tools
{
    /// <summary>
    /// Finds concrete subclasses of a base type across the loaded assemblies, by name.
    ///
    /// <para>WHY THIS EXISTS AS A HELPER. Nebula has two extension points a game plugs into by
    /// writing a subclass in its own project and naming it on the command line or in an editor
    /// configuration: <c>BotBehavior</c> (scripted real clients) and <c>SyntheticInputSource</c>
    /// (the load client's input). Both resolve by TYPE NAME rather than by script path, because a
    /// C# script resource cannot be instantiated through Godot's script API the way a GDScript one
    /// can - and because reflection is also what lets the editor offer a dropdown of the
    /// implementations that actually exist.</para>
    ///
    /// <para>Matching accepts either the full name or the short one, so a stored configuration can
    /// hold the readable short name while a project with colliding names can still disambiguate
    /// with the full one.</para>
    /// </summary>
    public static class TypeDiscovery
    {
        /// <summary>
        /// Finds a concrete <typeparamref name="TBase"/> subclass by full name or short name, or
        /// null when nothing matches. A full-name match wins outright; a short-name match is only
        /// returned once every candidate has been considered.
        /// </summary>
        public static Type Resolve<TBase>(string name)
        {
            Type shortNameMatch = null;
            foreach (var candidate in Discover<TBase>())
            {
                if (candidate.FullName == name)
                    return candidate;
                if (candidate.Name == name)
                    shortNameMatch = candidate;
            }
            return shortNameMatch;
        }

        /// <summary>
        /// Every concrete <typeparamref name="TBase"/> subclass in the loaded assemblies. Used both
        /// to resolve a configured name and to populate the editor's dropdowns.
        /// </summary>
        public static List<Type> Discover<TBase>()
        {
            var found = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // A partially-loadable assembly still yields the types that did load, which is
                    // enough - and is better than letting one bad reference hide every candidate.
                    types = ex.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract) continue;
                    if (!typeof(TBase).IsAssignableFrom(type)) continue;
                    found.Add(type);
                }
            }
            return found;
        }
    }
}
