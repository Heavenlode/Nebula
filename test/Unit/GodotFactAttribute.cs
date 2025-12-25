using System;

namespace NebulaTests.Unit;

/// <summary>
/// Marks a method as a unit test that runs inside Godot.
/// These tests are discovered and run by UnitTestRunner, not by xUnit directly.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class GodotFactAttribute : Attribute
{
}

