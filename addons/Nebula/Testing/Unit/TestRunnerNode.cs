#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Nebula.Testing.Unit;

/// <summary>
/// Godot scene that discovers and runs classes marked with [NebulaUnitTest] attribute.
/// Supports --discover flag to list tests without running them.
/// </summary>
public partial class TestRunnerNode : Node
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
            GetTree().Quit(0);
            return;
        }

        RunAllTestsThenQuit();
    }

    /// <summary>
    /// async void, deliberately: _Ready cannot await, and the suite must be able to span engine
    /// frames. Async tests resume on the main thread via Godot's SynchronizationContext between
    /// frames -- which is also what lets a test run work on a worker thread: synchronous
    /// RenderingServer/ResourceLoader calls made from a worker are serviced only when the main
    /// thread pumps, so a runner that blocked the main thread waiting on such a test would
    /// deadlock the whole suite.
    /// </summary>
    private async void RunAllTestsThenQuit()
    {
        try
        {
            await RunAllTests();
        }
        catch (Exception ex)
        {
            GD.Print($"[FAIL] TestRunnerNode: runner crashed: {ex.Message}");
            _failed++;
        }

        // Exit with appropriate code
        GetTree().Quit(_failed > 0 ? 1 : 0);
    }

    private void DiscoverTests()
    {
        GD.Print("[DISCOVER_START]");

        var assembly = Assembly.GetExecutingAssembly();
        var testClasses = assembly.GetTypes()
            .Where(t => t.IsClass &&
                       !t.IsAbstract &&
                       t.GetCustomAttribute<NebulaUnitTestAttribute>() != null);

        foreach (var testClass in testClasses)
        {
            var testMethods = testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<NebulaUnitTestAttribute>() != null);

            foreach (var method in testMethods)
            {
                GD.Print($"[TEST] {testClass.Name}.{method.Name}");
            }
        }

        GD.Print("[DISCOVER_END]");
    }

    private async Task RunAllTests()
    {
        GD.Print("[RUN_START]");

        var assembly = Assembly.GetExecutingAssembly();

        // Find all test classes marked with [NebulaUnitTest] attribute
        var testClasses = assembly.GetTypes()
            .Where(t => t.IsClass &&
                       !t.IsAbstract &&
                       t.GetCustomAttribute<NebulaUnitTestAttribute>() != null);

        foreach (var testClass in testClasses)
        {
            await RunTestClass(testClass);
        }

        GD.Print("[RUN_END]");
        GD.Print($"[SUMMARY] Passed: {_passed}, Failed: {_failed}");
    }

    private async Task RunTestClass(Type testClass)
    {
        // Find all methods with [NebulaUnitTest] attribute
        var testMethods = testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<NebulaUnitTestAttribute>() != null);

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
            await RunTestMethod(testClass, instance!, method);
        }

        // Dispose if IDisposable
        if (instance is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Ceiling for a single Task-returning test. Generous because async tests exist precisely for
    /// work that spans frames (worker-thread generation, multi-frame settling); its job is only to
    /// turn a wedged test into a [FAIL] instead of a suite that never prints a summary.
    /// </summary>
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(120);

    private async Task RunTestMethod(Type testClass, object instance, MethodInfo method)
    {
        var testName = $"{testClass.Name}.{method.Name}";

        try
        {
            var result = method.Invoke(instance, null);

            // A Task-returning test is awaited here on the main thread, which keeps pumping engine
            // frames between continuations. Do NOT be tempted to .Wait()/.GetResult() it: a test
            // whose worker thread makes a synchronous RenderingServer/ResourceLoader call needs a
            // live main thread to service that call, and blocking here deadlocks the suite.
            if (result is Task task)
            {
                var finished = await Task.WhenAny(task, Task.Delay(AsyncTestTimeout));
                if (finished != task)
                {
                    GD.Print($"[FAIL] {testName}: timed out after {AsyncTestTimeout.TotalSeconds:0}s");
                    _failed++;
                    return;
                }
                await task; // Propagate the test's exception, if any.
            }

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
