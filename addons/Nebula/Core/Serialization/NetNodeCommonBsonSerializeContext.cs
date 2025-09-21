using Godot;
using System.Collections.Generic;
using System;

namespace Nebula.Serialization
{
    public partial class NetNodeCommonBsonSerializeContext : RefCounted
    {
        public bool Recurse = true;
        public Callable NodeFilter = new();
        public HashSet<Tuple<Variant.Type, string>> PropTypes = [];
        public HashSet<Tuple<Variant.Type, string>> SkipPropTypes = [];
        public Variant PropContext = new();
    }

}