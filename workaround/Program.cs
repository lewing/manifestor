
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace acquire
{
    class Program
    {
        const string ManifestVersion = "6.0.100";
        
        static string Rid = GetRid();

        static string MuxerPath { get; } = GetDotnetPath();

        static string PackageName = "Microsoft.NET.Sdk.BlazorWebAssembly.AOT";
        static string PackageVersion = "6.0.0-*";

        static string WorkloadName = "Microsoft.NET.Workload.BlazorWebAssembly";
        static string WorkloadId = null;
        
        static Dictionary<string,string> Versions { get; } = new Dictionary<string,string>();


        static string GetDotnetPath()
        {
            // Process.MainModule is app[.exe] and not `dotnet`. We can instead calculate the dotnet SDK path
            // by looking at the shared fx directory instead.
            // depsFile = /dotnet/shared/Microsoft.NETCore.App/6.0-preview2/Microsoft.NETCore.App.deps.json
            var depsFile = (string)AppContext.GetData("FX_DEPS_FILE");
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(depsFile), "..", "..", "..", "dotnet" + (OperatingSystem.IsWindows() ? ".exe" : "")));
        }

        static string GetRid ()
        {
            if (OperatingSystem.IsWindows())
                return Environment.Is64BitProcess ? "win-x64": "win-x86";
            else if (OperatingSystem.IsMacOS())
                return "osx-x64";
            else if (OperatingSystem.IsLinux())
                return "linux-x64";
            else
            {
                Console.Error.WriteLine("Unsupported platform.");
                return "any";
            }
        }

        static int Main(string[] args)
        {
            //System.Console.WriteLine(MuxerPath);
            string sdkDirectory = Path.GetDirectoryName(MuxerPath);
            var tempDirectory = Path.Combine(Directory.GetCurrentDirectory(), "tmp", Path.GetRandomFileName());
            var restoreDirectory = Path.Combine(tempDirectory, ".nuget");
            string manifestPath = null;

            foreach (var arg in args)
            {
                if (arg.StartsWith ('-')) {
                    var parts = arg.Split (':', 2);
                    var argName = parts[0];
                    var argValue = parts[1];
                    switch(argName)
                    {
                    case "-manifest":
                        manifestPath = argValue;
                        break;
                    case "-workload":
                        WorkloadName = argValue;
                        break;
                    case "-workloadId":
                        WorkloadId = argValue;
                        break;
                    case "-packageName":
                        PackageName = argValue;
                        break;
                    case "-packageVersion":
                        PackageVersion = argValue;
                        break;
                    case "-rid":
                        Rid = argValue;
                        break;
                    case "-v": 
                        var kvp = argValue.Split('=', 2);
                        if (kvp.Length != 2)
                        {
                            Console.Error.WriteLine ($"Malformed argument {argName}: {argValue}");
                            return 1;
                        }

                        if (!kvp[0].StartsWith("${") && !char.IsDigit(kvp[0][0]))
                            kvp[0] = $"${{{kvp[0]}}}";
                            
                        Versions[kvp[0]] = kvp[1];
                        break;
                    case "-sdkPath":
                        sdkDirectory = argValue;
                        break;
                    default:
                        Console.Error.WriteLine ($"Uknown argument: {argValue}");
                        return 1;
                    }
                }
                else
                {
                    Console.Error.WriteLine ($"Uknown argument: {arg}");
                    return 1;
                }
            }

            Console.WriteLine ($"Targeting SDK : :{sdkDirectory}");

            try
            {
                var restore = Restore(tempDirectory, restoreDirectory, manifestPath, out var packs);
                if (restore != 0)
                {
                    return restore;
                }

                var sourceManifestDirectory = Path.Combine(restoreDirectory, PackageName.ToLowerInvariant(), ManifestVersion);
                var targetManifestDirectory = Path.Combine(sdkDirectory, "sdk-manifests", ManifestVersion, WorkloadName);
                Move(sourceManifestDirectory, targetManifestDirectory);

                foreach (var (id, version) in packs)
                {
                    var source = Path.Combine(restoreDirectory, id.ToLowerInvariant(), version);
                    var destination = Path.Combine(sdkDirectory, "packs", id, version);

                    Move(source, destination);
                }

                var sdkVersionProc = Process.Start(new ProcessStartInfo
                {
                    FileName = MuxerPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                });
                
                sdkVersionProc.WaitForExit();
                var sdkVersion = sdkVersionProc.StandardOutput.ReadToEnd().Trim();
                var sentinelPath = Path.Combine(sdkDirectory, "sdk", sdkVersion, "EnableWorkloadResolver.sentinel");
                Console.WriteLine($"Writing sentinel to {sentinelPath}.");

                File.WriteAllBytes(sentinelPath, Array.Empty<byte>());
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }

            return 0;
        }

        static void Move(string source, string destination)
        {
            Console.WriteLine($"Moving {source} to {destination}...");
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            Directory.Move(source, destination);
        }

        static int Restore(string tempDirectory, string restoreDirectory, string manifestPath, out List<(string, string)> packs)
        {
            packs = null;

            var restoreProject = Path.Combine(tempDirectory, "restore", "Restore.csproj");
            var restoreProjectDirectory = Directory.CreateDirectory(Path.GetDirectoryName(restoreProject));

            File.WriteAllText(Path.Combine(restoreProjectDirectory.FullName, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(restoreProjectDirectory.FullName, "Directory.Build.targets"), "<Project />");

            var projectFile = $@"
<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>net6.0</TargetFramework>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include=""{PackageName}"" Version=""{PackageVersion}"" />
    </ItemGroup>
</Project>
";
            File.WriteAllText(restoreProject, projectFile);

            Console.WriteLine("Restoring...");

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = MuxerPath,
                ArgumentList = { "restore", restoreProject },
                Environment =
                {
                    ["NUGET_PACKAGES"] = restoreDirectory,
                },
            });
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"Unable to restore {PackageName} workload.");
                return 1;
            }

            var manifestDirectory = Path.Combine(restoreDirectory, PackageName.ToLowerInvariant());
            var version = Directory.EnumerateDirectories(manifestDirectory).First();

            manifestDirectory = Path.Combine(manifestDirectory, ManifestVersion);
            Directory.Move(version, manifestDirectory);

            if (manifestPath != null)
            {   
                var sourceDirectory = Path.GetDirectoryName(manifestPath);
                if (Directory.Exists(manifestPath))
                {
                    sourceDirectory = manifestPath;
                    manifestPath = Path.Combine(manifestDirectory, "WorkloadManifest.json");
                }
                var targetsPath = Path.Combine(sourceDirectory, "WorkloadManifest.targets");
                if (File.Exists(targetsPath))
                {
                    Console.WriteLine ($"⚠️ Copying targets : {targetsPath} -> {Path.Combine(manifestDirectory, "WorkloadManifest.targets")}");
                    File.Copy(targetsPath, Path.Combine(manifestDirectory, "WorkloadManifest.targets"), true);
                }
            }

            manifestPath = manifestPath ?? Path.Combine(manifestDirectory, "WorkloadManifest.json");
            var manifest = JsonSerializer.Deserialize<ManifestInformation>(File.ReadAllBytes(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Allow });
            foreach (var item in manifest.Packs)
            {
                if (Versions.TryGetValue(item.Value.Version, out var packVersion))
                    item.Value.Version = packVersion;
            }
            File.WriteAllText(Path.Combine(manifestDirectory, "WorkloadManifest.json"), JsonSerializer.Serialize (manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));

            projectFile = @"
<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>net6.0</TargetFramework>
        <NoWarn>$(NoWarn);NU1213</NoWarn>
    </PropertyGroup>
    <ItemGroup>
";
            packs = new List<(string id, string version)>();
            var subset = (WorkloadId == null ? null : manifest.Workloads[WorkloadId])?.Packs ?? null;

            foreach (var item in manifest.Packs)
            {
                if (subset != null && !subset.Contains(item.Key))
                    continue;
            
                var packageName = item.Key;
                if (item.Value.AliasTo is Dictionary<string, string> alias)
                {
                    packageName = "";
                    alias.TryGetValue(Rid, out packageName);
                }

                if (!string.IsNullOrEmpty(packageName)) {
                    var name = packageName.Contains("cross") && false ? "FrameworkReference" : "PackageReference";
                    projectFile += $"<{name} Include=\"{packageName}\" Version=\"{item.Value.Version}\" />";
                    packs.Add((packageName, item.Value.Version));
                }
            }

            projectFile += @"
    </ItemGroup>
</Project>
";
            File.WriteAllText(restoreProject, projectFile);

            process = Process.Start(new ProcessStartInfo
            {
                FileName = MuxerPath,
                ArgumentList = { "restore", restoreProject },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                Environment =
                {
                    ["NUGET_PACKAGES"] = restoreDirectory,
                },
            });
            process.WaitForExit();


            return 0;
        }

        private record ManifestInformation {
            public int Version { get; init; }
            public string Description {get; init; }

            [property: JsonPropertyName("depends-on")]
            public IDictionary<string, int> DependsOn { get; init; }
            public IDictionary<string, WorkloadInformation> Workloads { get; init; }
            public IDictionary<string, PackVersionInformation> Packs { get; init; }
            public object Data { get; init; }
        }

        private record WorkloadInformation {
            public bool Abstract { get; init; }
            public string Kind { get; init; }
            public string Description { get; init; }

            public List<string> Packs { get; init; }
            public List<string> Extends { get; init; }
            public List<string> Platforms { get; init; }

        }
        
        private record PackVersionInformation {
            public string Kind { get; init; }
            public string Version { get; set; }
            [property: JsonPropertyName("alias-to")]
            public Dictionary<string, string> AliasTo { get; init; }
        }
    }
}

