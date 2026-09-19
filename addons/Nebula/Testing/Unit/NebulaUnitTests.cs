#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Runs all [NebulaUnitTest] unit tests inside Godot and reports each as an individual xUnit test.
/// Uses [Theory] + caching so Godot only spawns once for all tests.
/// </summary>
[Collection("Nebula")]
public class NebulaUnitTests
{
    private static Dictionary<string, TestResult>? _cachedResults;
    private static List<string>? _cachedTestNames;
    private static readonly object _lock = new();
    private static bool _hasRun = false;

    /// <summary>
    /// The whole in-engine suite runs in this one spawn, so the budget covers every test at once.
    /// Its job is to turn a wedged suite into a failure with output attached, well inside the CI
    /// job limit, rather than a run that stops printing and is killed hours later.
    /// </summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Discovery only reflects over the assembly and quits; it never runs a test.</summary>
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Discovers test names by spawning Godot with --discover flag.
    /// </summary>
    public static IEnumerable<object[]> TestCases
    {
        get
        {
            lock (_lock)
            {
                _cachedTestNames ??= DiscoverTests();
            }
            return _cachedTestNames.Select(name => new object[] { name });
        }
    }

    [Theory]
    [MemberData(nameof(TestCases))]
    public void RunTest(string testName)
    {
        lock (_lock)
        {
            if (!_hasRun)
            {
                // First test - run ALL tests in Godot, cache results
                _cachedResults = RunAllTests();
                _hasRun = true;
            }
        }

        if (_cachedResults == null)
        {
            throw new Exception("Failed to run Godot tests - no results cached");
        }

        if (!_cachedResults.TryGetValue(testName, out var result))
        {
            throw new Exception($"Test '{testName}' was not found in Godot output");
        }

        if (!result.Passed)
        {
            throw new Exception(result.ErrorMessage ?? "Test failed");
        }
    }

    private static List<string> DiscoverTests()
    {
        var tests = new List<string>();

        var testProjectPath = HeadlessGodot.FindTestProjectPath();
        if (testProjectPath == null)
        {
            return tests;
        }

        try
        {
            var result = HeadlessGodot.Run(
                $"--path \"{testProjectPath}\" --headless res://addons/Nebula/Testing/Unit/TestRunnerNode.tscn --discover",
                DiscoverTimeout);

            foreach (var line in result.StandardOutput.Split('\n'))
            {
                if (line.StartsWith("[TEST]"))
                {
                    var testName = line.Substring("[TEST]".Length).Trim();
                    tests.Add(testName);
                }
            }
        }
        catch
        {
            // Return empty - tests will fail with clear message
        }

        return tests;
    }

    private static Dictionary<string, TestResult> RunAllTests()
    {
        var results = new Dictionary<string, TestResult>();

        var testProjectPath = HeadlessGodot.FindTestProjectPath();
        if (testProjectPath == null)
        {
            throw new Exception("Could not find test project path (project.godot)");
        }

        var run = HeadlessGodot.Run(
            $"--path \"{testProjectPath}\" --headless res://addons/Nebula/Testing/Unit/TestRunnerNode.tscn",
            RunTimeout);

        // Parse results
        foreach (var line in run.StandardOutput.Split('\n'))
        {
            if (line.StartsWith("[PASS]"))
            {
                var testName = line.Substring("[PASS]".Length).Trim();
                results[testName] = new TestResult { Passed = true };
            }
            else if (line.StartsWith("[FAIL]"))
            {
                var content = line.Substring("[FAIL]".Length).Trim();
                var colonIndex = content.IndexOf(':');
                if (colonIndex > 0)
                {
                    var testName = content.Substring(0, colonIndex).Trim();
                    var errorMessage = content.Substring(colonIndex + 1).Trim();
                    results[testName] = new TestResult { Passed = false, ErrorMessage = errorMessage };
                }
                else
                {
                    results[content] = new TestResult { Passed = false, ErrorMessage = "Test failed" };
                }
            }
        }

        // A run that printed no verdict at all crashed before the suite started - a protocol
        // mismatch, a load error. Every RunTest would otherwise report the same misleading
        // "was not found in Godot output" with nothing to go on.
        if (results.Count == 0)
        {
            throw new Exception(
                $"The in-engine suite reported no results (exit code {run.ExitCode}).\n" +
                $"Output:\n{run.StandardOutput}\n" +
                $"Stderr:\n{run.StandardError}");
        }

        return results;
    }

    private class TestResult
    {
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
