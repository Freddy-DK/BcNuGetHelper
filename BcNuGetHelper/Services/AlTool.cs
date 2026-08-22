using System.Diagnostics;
using System.Text.Json;
using BcNuGetHelper.Models;

namespace BcNuGetHelper.Services;

public class AlToolException(string message) : Exception(message);

/// <summary>
/// Wraps the AL development tools (altool) bundled with the deployment.
/// No fallback: the tool must be present or all operations fail.
/// </summary>
public class AlTool
{
    private static readonly Lazy<string> AlToolDll = new(() =>
    {
        var root = Path.Combine(AppContext.BaseDirectory, "altool");
        var dll = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "altool.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        return dll ?? throw new InvalidOperationException(
            $"altool.dll not found under '{root}'. The Microsoft.Dynamics.BusinessCentral.Development.Tools package must be deployed with the app.");
    });

    public async Task<AppManifest> GetPackageManifestAsync(string appFilePath, CancellationToken ct)
    {
        var json = await RunAsync(["GetPackageManifest", appFilePath], ct);
        return ParseManifest(json);
    }

    public async Task CreateSymbolPackageAsync(string appFilePath, string symbolFilePath, CancellationToken ct)
    {
        await RunAsync(["CreateSymbolPackage", appFilePath, symbolFilePath], ct);
        if (!File.Exists(symbolFilePath))
        {
            throw new AlToolException("altool did not produce a symbol package.");
        }
    }

    private static async Task<string> RunAsync(string[] arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // altool targets an older runtime than the app
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        psi.ArgumentList.Add(AlToolDll.Value);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new AlToolException("Failed to start altool.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var error = $"{(await stderrTask).Trim()} {(await stdoutTask).Trim()}".Trim();
            throw new AlToolException($"altool {arguments[0]} failed ({process.ExitCode}): {error}");
        }
        return await stdoutTask;
    }

    private static AppManifest ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var dependencies = new List<AppDependency>();
            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in deps.EnumerateArray())
                {
                    dependencies.Add(new AppDependency(
                        Guid.Parse(GetString(dep, "id")
                            ?? GetString(dep, "appId")
                            ?? throw new AlToolException("Dependency without id in manifest.")),
                        GetString(dep, "name") ?? "",
                        GetString(dep, "publisher") ?? "",
                        GetString(dep, "version") ?? GetString(dep, "minVersion") ?? "0.0.0.0"));
                }
            }

            return new AppManifest(
                Guid.Parse(GetString(root, "id") ?? throw new AlToolException("Manifest without id.")),
                GetString(root, "name") ?? "",
                GetString(root, "publisher") ?? "",
                GetString(root, "version") ?? throw new AlToolException("Manifest without version."),
                GetString(root, "description") ?? "",
                dependencies);
        }
        catch (JsonException ex)
        {
            throw new AlToolException($"Could not parse altool manifest output: {ex.Message}");
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
