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
        private static readonly Regex IdRegex = new(@"id=""?([^""\]]+)""?", RegexOptions.Compiled);

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
            var parts = line.Split(' ');
            
            foreach (var part in parts)
            {
                if (part.StartsWith("load_steps="))
                {
                    if (int.TryParse(ExtractValue(part), out var steps))
                        scene.LoadSteps = steps;
                }
                else if (part.StartsWith("format="))
                {
                    if (int.TryParse(ExtractValue(part), out var format))
                        scene.Format = format;
                }
                else if (part.StartsWith("uid="))
                {
                    scene.Uid = ExtractValue(part);
                }
            }
            
            return scene;
        }

        private static ExtResource ParseExtResource(string line)
        {
            var resource = new ExtResource();
            var parts = line.Split(' ');
            
            foreach (var part in parts)
            {
                if (part.StartsWith("type="))
                {
                    resource.Type = ExtractValue(part);
                }
                else if (part.StartsWith("path="))
                {
                    resource.Path = ExtractValue(part);
                }
                else if (part.StartsWith("id="))
                {
                    var match = IdRegex.Match(part);
                    if (match.Success)
                    {
                        resource.Id = match.Groups[1].Value;
                    }
                }
            }
            
            return resource;
        }

        private static SubResource ParseSubResource(string line)
        {
            var resource = new SubResource();
            var parts = line.Split(' ');
            
            foreach (var part in parts)
            {
                if (part.StartsWith("type="))
                {
                    resource.Type = ExtractValue(part);
                }
                else if (part.StartsWith("id="))
                {
                    resource.Id = ExtractValue(part);
                }
            }
            
            return resource;
        }

        private TscnNode ParseNode(string line)
        {
            var node = new TscnNode();
            var parts = line.Split(' ');
            
            foreach (var part in parts)
            {
                if (part.StartsWith("name="))
                {
                    node.Name = ExtractValue(part);
                }
                else if (part.StartsWith("type="))
                {
                    node.Type = ExtractValue(part);
                }
                else if (part.StartsWith("parent="))
                {
                    node.Parent = ExtractValue(part);
                }
                else if (part.StartsWith("instance="))
                {
                    var instValue = part.Substring("instance=".Length).Trim('"', ']');
                    var match = ExtResourceRegex.Match(instValue);
                    if (match.Success)
                    {
                        var resourceId = match.Groups[1].Value;
                        if (_resourceToPathMap.TryGetValue(resourceId, out var path) && path.EndsWith(".tscn"))
                        {
                            node.Instance = path;
                        }
                    }
                }
            }
            
            return node;
        }

        private static string ExtractValue(string part)
        {
            var eqIndex = part.IndexOf('=');
            if (eqIndex < 0) return "";
            return part.Substring(eqIndex + 1).Trim('"', ']', ' ');
        }
    }
}
