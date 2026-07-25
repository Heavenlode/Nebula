#nullable enable
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Nebula.Generators
{
    /// <summary>
    /// Parser for Godot .tscn scene files. Pure C# implementation for source generators.
    /// </summary>
    internal sealed class TscnParser
    {
        private readonly Dictionary<string, string> _resourceToPathMap = new();

        public sealed class GdScene
        {
            public int LoadSteps { get; set; }
            public int Format { get; set; }
            public string? Uid { get; set; }
        }

        public sealed class ExtResource
        {
            public string Type { get; set; } = "";
            public string Path { get; set; } = "";
            public string Id { get; set; } = "";
        }

        public sealed class SubResource
        {
            public string Type { get; set; } = "";
            public string Id { get; set; } = "";
            public Dictionary<string, string> Properties { get; } = new();
        }

        public sealed class TscnNode
        {
            public string Name { get; set; } = "";
            public string? Type { get; set; }
            public string? Parent { get; set; }
            public string? Instance { get; set; }
            public Dictionary<string, string> Properties { get; } = new();
        }

        public sealed class ParsedTscn
        {
            public GdScene? GdScene { get; set; }
            public List<ExtResource> ExtResources { get; } = new();
            public List<SubResource> SubResources { get; } = new();
            public List<TscnNode> Nodes { get; } = new();
            public TscnNode? RootNode { get; set; }
        }

        private static readonly Regex ExtResourceRegex = new(@"ExtResource\(""([^""]+)""\)", RegexOptions.Compiled);
        /// <summary>
        /// Matches key="quoted value" or key=unquotedToken on a .tscn header line.
        /// Quoted values may contain spaces (e.g. path="res://NPC/Big Fooder/X.cs").
        /// </summary>
        private static readonly Regex AttributeRegex = new(
            @"(\w+)=(""[^""]*""|[^\s\]]+)",
            RegexOptions.Compiled);

        public ParsedTscn Parse(string fileText)
        {
            _resourceToPathMap.Clear();
            var result = new ParsedTscn();
            var lines = fileText.Split('\n');
            
            SubResource? currentSubResource = null;
            TscnNode? currentNode = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("[gd_scene"))
                {
                    result.GdScene = ParseGdScene(line);
                    currentSubResource = null;
                    currentNode = null;
                }
                else if (line.StartsWith("[ext_resource"))
                {
                    var extRes = ParseExtResource(line);
                    result.ExtResources.Add(extRes);
                    _resourceToPathMap[extRes.Id] = extRes.Path;
                    currentSubResource = null;
                    currentNode = null;
                }
                else if (line.StartsWith("[sub_resource"))
                {
                    currentSubResource = ParseSubResource(line);
                    result.SubResources.Add(currentSubResource);
                    currentNode = null;
                }
                else if (line.StartsWith("[node"))
                {
                    currentNode = ParseNode(line);
                    result.Nodes.Add(currentNode);
                    if (currentNode.Parent == null)
                    {
                        result.RootNode = currentNode;
                    }
                    currentSubResource = null;
                }
                else if (line.StartsWith("[")) 
                {
                    // Other section types we don't care about
                    currentSubResource = null;
                    currentNode = null;
                }
                else if (line.Contains("="))
                {
                    var eqIndex = line.IndexOf('=');
                    var propName = line.Substring(0, eqIndex).Trim();
                    var propValue = line.Substring(eqIndex + 1).Trim();

                    if (currentSubResource != null)
                    {
                        currentSubResource.Properties[propName] = propValue;
                    }
                    else if (currentNode != null)
                    {
                        // Resolve ExtResource references for script property
                        if (propName == "script")
                        {
                            var match = ExtResourceRegex.Match(propValue);
                            if (match.Success)
                            {
                                var resourceId = match.Groups[1].Value;
                                if (_resourceToPathMap.TryGetValue(resourceId, out var path))
                                {
                                    propValue = path;
                                }
                            }
                        }
                        currentNode.Properties[propName] = propValue;
                    }
                }
            }

            return result;
        }

        private static GdScene ParseGdScene(string line)
        {
            var scene = new GdScene();
            foreach (var (key, value) in ExtractAttributes(line))
            {
                if (key == "load_steps" && int.TryParse(value, out var steps))
                    scene.LoadSteps = steps;
                else if (key == "format" && int.TryParse(value, out var format))
                    scene.Format = format;
                else if (key == "uid")
                    scene.Uid = value;
            }
            return scene;
        }

        private static ExtResource ParseExtResource(string line)
        {
            var resource = new ExtResource();
            foreach (var (key, value) in ExtractAttributes(line))
            {
                if (key == "type")
                    resource.Type = value;
                else if (key == "path")
                    resource.Path = value;
                else if (key == "id")
                    resource.Id = value;
            }
            return resource;
        }

        private static SubResource ParseSubResource(string line)
        {
            var resource = new SubResource();
            foreach (var (key, value) in ExtractAttributes(line))
            {
                if (key == "type")
                    resource.Type = value;
                else if (key == "id")
                    resource.Id = value;
            }
            return resource;
        }

        private TscnNode ParseNode(string line)
        {
            var node = new TscnNode();
            foreach (var (key, value) in ExtractAttributes(line))
            {
                if (key == "name")
                    node.Name = value;
                else if (key == "type")
                    node.Type = value;
                else if (key == "parent")
                    node.Parent = value;
                else if (key == "instance")
                {
                    var match = ExtResourceRegex.Match(value);
                    if (match.Success)
                    {
                        var resourceId = match.Groups[1].Value;
                        if (_resourceToPathMap.TryGetValue(resourceId, out var path) && path.EndsWith(".tscn"))
                            node.Instance = path;
                    }
                }
            }
            return node;
        }

        /// <summary>
        /// Yields (key, unquotedValue) pairs from a .tscn header line such as
        /// [ext_resource type="Script" path="res://foo bar/x.cs" id="1_abc"].
        /// Does not split inside double-quoted values, so paths with spaces parse correctly.
        /// </summary>
        private static IEnumerable<(string Key, string Value)> ExtractAttributes(string line)
        {
            foreach (Match match in AttributeRegex.Matches(line))
            {
                var key = match.Groups[1].Value;
                var raw = match.Groups[2].Value;
                var value = raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"'
                    ? raw.Substring(1, raw.Length - 2)
                    : raw.TrimEnd(']');
                yield return (key, value);
            }
        }
    }
}
