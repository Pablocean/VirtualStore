using System.Diagnostics;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// Fact that is skipped at discovery time when no Docker daemon is available.
/// xUnit v2 has no runtime dynamic skip (SkipException.ForSkip only works on v3+),
/// so Docker readiness must be evaluated here: setting <see cref="FactAttribute.Skip"/>
/// makes the runner report the test as Skipped instead of Failed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
            Skip = "Docker unavailable: MongoDB Testcontainer could not start.";
    }
}

/// <summary>
/// Probes Docker daemon readiness once per test run via <c>docker info</c>.
/// Honors the VIRTUALSTORE_DOCKER_AVAILABLE override (1/true or 0/false).
/// </summary>
internal static class DockerAvailability
{
    private static readonly Lazy<bool> _available = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => _available.Value;

    private static bool Probe()
    {
        var forced = Environment.GetEnvironmentVariable("VIRTUALSTORE_DOCKER_AVAILABLE");
        if (forced is "1" or "true" or "TRUE" or "True")
            return true;
        if (forced is "0" or "false" or "FALSE" or "False")
            return false;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "info",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            if (!process.Start())
                return false;
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                try { process.Kill(); } catch { /* already exiting */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            // No docker CLI, no daemon, or process start denied.
            return false;
        }
    }
}
