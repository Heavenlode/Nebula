#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NebulaTests.Unit;

/// <summary>
/// Godot scene that discovers and runs [GodotFact] tests in the Unit namespace.
/// Supports --discover flag to list tests without running them.
/// </summary>
public partial class UnitTestRunner : Node
{
    private int _passed = 0;
    private int _failed = 0;
    private List<string> _failures = new();
    private bool _discoverOnly = false;

    public override void _Ready()
    {
        // Check for --discover flag
        foreach (var arg in OS.GetCmdlineArgs())
        {
            if (arg == "--discover")
            {
                _discoverOnly = true;
                break;
            }
        }

        if (_discoverOnly)
        {
            DiscoverTests();
        }
        else
        {
            RunAllTests();
        }
        
        // Exit with appropriate code
        GetTree().Quit(_failed > 0 ? 1 : 0);
    }

    private void DiscoverTests()
    {
        GD.Print("[DISCOVER_START]");
        
        var assembly = Assembly.GetExecutingAssembly();
        var testClasses = assembly.GetTypes()
            .Where(t => t.Namespace != null && 
                       t.Namespace.StartsWith("NebulaTests.Unit") &&
                       t.IsClass && 
                       !t.IsAbstract &&
                       t.Name != "UnitTestRunner");

        foreach (var testClass in testClasses)
        {
            var testMethods = testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<GodotFactAttribute>() != null);

            foreach (var method in testMethods)
            {
                GD.Print($"[TEST] {testClass.Name}.{method.Name}");
            }
        }
        
        GD.Print("[DISCOVER_END]");
    }

    private void RunAllTests()
    {
        GD.Print("[RUN_START]");
        
        var assembly = Assembly.GetExecutingAssembly();
        
        // Find all test classes in the Unit namespace
        var testClasses = assembly.GetTypes()
            .Where(t => t.Namespace != null && 
                       t.Namespace.StartsWith("NebulaTests.Unit") &&
                       t.IsClass && 
                       !t.IsAbstract &&
                       t.Name != "UnitTestRunner");

        foreach (var testClass in testClasses)
        {
            RunTestClass(testClass);
        }
        
        GD.Print("[RUN_END]");
        GD.Print($"[SUMMARY] Passed: {_passed}, Failed: {_failed}");
    }

    private void RunTestClass(Type testClass)
    {
        // Find all methods with [GodotFact] attribute
        var testMethods = testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<GodotFactAttribute>() != null);

        if (!testMethods.Any())
            return;

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(testClass);
        }
        catch (Exception ex)
        {
            // Report failure for all methods in this class
            foreach (var method in testMethods)
            {
                var testName = $"{testClass.Name}.{method.Name}";
                GD.Print($"[FAIL] {testName}: Failed to create instance: {ex.Message}");
                _failed++;
            }
            return;
        }

        foreach (var method in testMethods)
        {
            RunTestMethod(testClass, instance!, method);
        }

        // Dispose if IDisposable
        if (instance is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }
    }

    private void RunTestMethod(Type testClass, object instance, MethodInfo method)
    {
        var testName = $"{testClass.Name}.{method.Name}";
        
        try
        {
            method.Invoke(instance, null);
            GD.Print($"[PASS] {testName}");
            _passed++;
        }
        catch (TargetInvocationException ex)
        {
            var innerEx = ex.InnerException ?? ex;
            GD.Print($"[FAIL] {testName}: {innerEx.Message}");
            _failed++;
        }
        catch (Exception ex)
        {
            GD.Print($"[FAIL] {testName}: {ex.Message}");
            _failed++;
        }
    }
}
