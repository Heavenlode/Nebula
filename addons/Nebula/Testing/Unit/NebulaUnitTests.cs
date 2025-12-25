#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        var godotBin = Environment.GetEnvironmentVariable("GODOT_BIN");
        
        if (string.IsNullOrEmpty(godotBin))
        {
            // Return empty list - tests will fail with clear message
            return tests;
        }

        var testProjectPath = FindTestProjectPath();
        if (testProjectPath == null)
        {
            return tests;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = godotBin,
            Arguments = $"--path \"{testProjectPath}\" --headless res://addons/Nebula/Testing/Unit/TestRunnerNode.tscn --discover",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n'))
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
        var godotBin = Environment.GetEnvironmentVariable("GODOT_BIN");

        if (string.IsNullOrEmpty(godotBin))
        {
            throw new Exception("GODOT_BIN environment variable is not set");
        }

        var testProjectPath = FindTestProjectPath();
        if (testProjectPath == null)
        {
            throw new Exception("Could not find test project path (project.godot)");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = godotBin,
            Arguments = $"--path \"{testProjectPath}\" --headless res://addons/Nebula/Testing/Unit/TestRunnerNode.tscn",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // Parse results
        foreach (var line in output.Split('\n'))
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

        return results;
    }

    private static string? FindTestProjectPath()
    {
        // Try to find from base directory
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var projectFile = Path.Combine(dir, "project.godot");
            if (File.Exists(projectFile))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        // Fallback - try current directory
        var workspaceRoot = Environment.CurrentDirectory;
        var testPath = Path.Combine(workspaceRoot, "test");
        if (Directory.Exists(testPath) && File.Exists(Path.Combine(testPath, "project.godot")))
        {
            return testPath;
        }

        return null;
    }

    private class TestResult
    {
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
    }
}


