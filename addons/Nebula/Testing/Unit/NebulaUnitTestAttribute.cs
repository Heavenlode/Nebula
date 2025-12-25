using System;

namespace Nebula.Testing.Unit;

/// <summary>
/// Marks a method or class as a unit test that runs inside Godot.
/// These tests are discovered and run by TestRunnerNode, not by xUnit directly.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public class NebulaUnitTestAttribute : Attribute
{
}

