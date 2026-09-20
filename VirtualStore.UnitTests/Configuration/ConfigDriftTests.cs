using System.Text.Json;
using Xunit;

namespace VirtualStore.UnitTests.Configuration;

/// <summary>
/// Wave 3a-D: config single-source-of-truth guard.
/// Every leaf key in appsettings.json (+ appsettings.Development.json), excluding
/// Serilog/Logging/AllowedHosts, must have a __-joined counterpart in .env.example,
/// and vice versa (no orphan .env keys).
/// </summary>
public class ConfigDriftTests
{
    private static readonly string[] ExcludedTopLevel = ["Serilog", "Logging", "AllowedHosts"];

    private static string FindApiDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "VirtualStore.API", "appsettings.json");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "VirtualStore.API");
            var direct = Path.Combine(dir.FullName, "appsettings.json");
            if (File.Exists(direct) && dir.Name == "VirtualStore.API")
                return dir.FullName;
            dir = dir.Parent;
        }

        // Fallback: walk up from the current directory.
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "VirtualStore.API", "appsettings.json");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "VirtualStore.API");
            var direct = Path.Combine(dir.FullName, "appsettings.json");
            if (File.Exists(direct) && dir.Name == "VirtualStore.API")
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate VirtualStore.API/appsettings.json from test output or current directory.");
    }

    private static string FindEnvExamplePath(string apiDir)
    {
        // .env.example lives at the repo root (parent of VirtualStore.API),
        // but also probe the API dir itself for robustness.
        var inApiDir = Path.Combine(apiDir, ".env.example");
        if (File.Exists(inApiDir))
            return inApiDir;

        var dir = Directory.GetParent(apiDir);
        for (var i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, ".env.example");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(".env.example not found relative to the API project dir.", inApiDir);
    }

    private static HashSet<string> LoadAppsettingsLeafKeys(string apiDir)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var path = Path.Combine(apiDir, file);
            if (!File.Exists(path))
                continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            CollectLeaves(doc.RootElement, [], keys);
        }
        return keys;
    }

    private static void CollectLeaves(JsonElement element, List<string> prefix, HashSet<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    prefix.Add(prop.Name);
                    CollectLeaves(prop.Value, prefix, keys);
                    prefix.RemoveAt(prefix.Count - 1);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    prefix.Add(index.ToString());
                    CollectLeaves(item, prefix, keys);
                    prefix.RemoveAt(prefix.Count - 1);
                    index++;
                }
                break;
            default:
                if (prefix.Count == 0)
                    break;
                if (ExcludedTopLevel.Contains(prefix[0], StringComparer.Ordinal))
                    break;
                keys.Add(string.Join("__", prefix));
                break;
        }
    }

    private static HashSet<string> LoadEnvExampleKeys(string envExamplePath)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadAllLines(envExamplePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            if (key.Length == 0)
                continue;
            var top = key.Split("__", StringSplitOptions.None)[0];
            if (ExcludedTopLevel.Contains(top, StringComparer.Ordinal))
                continue;
            keys.Add(key);
        }
        return keys;
    }

    private static (HashSet<string> AppsettingsKeys, HashSet<string> EnvKeys) LoadBoth()
    {
        var apiDir = FindApiDir();
        var envPath = FindEnvExamplePath(apiDir);
        return (LoadAppsettingsLeafKeys(apiDir), LoadEnvExampleKeys(envPath));
    }

    [Fact]
    public void Appsettings_LeafKeys_AllExistInEnvExample()
    {
        var (appsettingsKeys, envKeys) = LoadBoth();
        var missing = appsettingsKeys.Where(k => !envKeys.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0,
            "appsettings leaf key(s) missing from .env.example: " + string.Join(", ", missing));
    }

    [Fact]
    public void EnvExample_Keys_NoOrphansOutsideAppsettings()
    {
        var (appsettingsKeys, envKeys) = LoadBoth();
        var orphans = envKeys.Where(k => !appsettingsKeys.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(orphans.Count == 0,
            ".env.example key(s) with no appsettings counterpart: " + string.Join(", ", orphans));
    }
}
