using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO.Enumeration;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Framework;
using Meziantou.Framework.DependencyScanning;
using Meziantou.Framework.Globbing;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DepsUpdater
{
    internal static class Program
    {
        private static readonly HttpClient s_httpClient = new();

        private static Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand();
            AddUpdateCommand(rootCommand);
            return rootCommand.InvokeAsync(args);
        }

        private static void AddUpdateCommand(RootCommand rootCommand)
        {
            var replaceValueCommand = new Command("update")
            {
                new Option<string>(
                    "--directory",
                    arity: ArgumentArity.ZeroOrOne,
                    description: "Root directory"),

                new Option<string>(
                    "--files",
                    arity: ArgumentArity.ZeroOrMore,
                    description: "Glob patterns to find files to update"),

                new Option<string>(
                    "--dependency-type",
                    arity: ArgumentArity.ZeroOrMore,
                    description: "NuGet, npm"),
            };

            replaceValueCommand.Description = "Update dependencies";
            replaceValueCommand.Handler = CommandHandler.Create((string directory, string[] files, string[] dependencyType) => ReplaceValue(directory, files, dependencyType));

            rootCommand.AddCommand(replaceValueCommand);
        }

        private static async Task<int> ReplaceValue(string directory, string[] filePatterns, string[] dependencyType)
        {
            GlobCollection? globs = null;
            if (filePatterns.Length > 0)
            {
                globs = new(filePatterns.WhereNotNull().Select(pattern => Glob.Parse(pattern, GlobOptions.IgnoreCase)).ToArray());
            }
            else
            {
                globs = new(Glob.Parse("**/*", GlobOptions.None), Glob.Parse("!**/node_modules/**/*", GlobOptions.None));
            }

            var dependencyTypes = dependencyType.WhereNotNull().Select(Enum.Parse<DependencyType>).ToArray();

            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (_, _) => cancellationTokenSource.Cancel();

            if (string.IsNullOrEmpty(directory))
            {
                directory = Environment.CurrentDirectory;
            }

            var options = new ScannerOptions()
            {
                RecurseSubdirectories = true,
                ShouldScanFilePredicate = (ref FileSystemEntry entry) => globs.IsMatch(ref entry),
                ShouldRecursePredicate = (ref FileSystemEntry entry) => globs.IsPartialMatch(ref entry),
            };

            var dependencies = await DependencyScanner.ScanDirectoryAsync(directory, options, cancellationTokenSource.Token).ToListAsync();
            Console.WriteLine($"{dependencies.Count} dependencies found");
            foreach (var dependency in dependencies.OrderBy(dep => dep.Type).ThenBy(dep => dep.Name).ThenBy(dep => dep.Version))
            {
                Console.WriteLine("- " + dependency);
            }

            foreach (var dependency in dependencies.Where(d => d.Location.IsUpdatable))
            {
                if (dependencyTypes.Length > 0 && !dependencyTypes.Contains(dependency.Type))
                    continue;

                string? updatedVersion = null;
                if (dependency.Type == DependencyType.NuGet)
                {
                    updatedVersion = await UpdateNuGetDependencyAsync(dependency, cancellationTokenSource.Token);
                }
                else if (dependency.Type == DependencyType.DotNetSdk)
                {
                    updatedVersion = await UpdateDotNetSDKDependencyAsync(dependency, cancellationTokenSource.Token);
                }
                else if (dependency.Type == DependencyType.Npm)
                {
                    updatedVersion = await UpdateNpmDependencyAsync(dependency, cancellationTokenSource.Token);
                }

                if (updatedVersion != null)
                {
                    Console.WriteLine($"Updated {dependency} -> {updatedVersion}");
                }
            }
            return 0;
        }

        private static async Task<string?> UpdateNuGetDependencyAsync(Dependency dependency, CancellationToken cancellationToken)
        {
            if (!NuGetVersion.TryParse(dependency.Version, out var currentVersion))
                return null;

            // https://github.com/NuGet/Samples/tree/main/NuGetProtocolSamples
            var cache = new SourceCacheContext() { NoCache = true };
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
            var versions = await resource.GetAllVersionsAsync(
                dependency.Name,
                cache,
                logger: NullLogger.Instance,
                cancellationToken);

            bool ConsiderVersion(NuGetVersion version)
            {
                // Only consider newer version
                if (version <= currentVersion)
                    return false;

                // Stable version are always considered
                if (version.IsPrerelease)
                {
                    // 1.0.0-alpha -> 1.0.0-beta OK
                    // 1.0.0-alpha -> 2.0.0      OK
                    // 1.0.0       -> 2.0.0-beta KO
                    // 1.0.0-alpha -> 2.0.0-beta KO
                    if (!currentVersion.IsPrerelease)
                        return false;

                    if ((version.Major, version.Minor, version.Patch) == (currentVersion.Major, currentVersion.Minor, currentVersion.Patch))
                        return true;
                }

                return true;
            }

            var latestVersion = versions.Where(ConsiderVersion).DefaultIfEmpty().Max();
            if (latestVersion == null)
                return null;

            await dependency.UpdateAsync(latestVersion.ToString(), cancellationToken);
            return latestVersion.ToString();
        }

        private static async Task<string?> UpdateDotNetSDKDependencyAsync(Dependency dependency, CancellationToken cancellationToken)
        {
            if (!SemanticVersion.TryParse(dependency.Version, out var currentVersion))
                return null;

            bool ConsiderVersion(SemanticVersion version)
            {
                // Only consider newer version
                if (version <= currentVersion)
                    return false;

                // Stable version are always considered
                if (version.IsPrerelease)
                {
                    // 1.0.0-alpha -> 1.0.0-beta OK
                    // 1.0.0-alpha -> 2.0.0      OK
                    // 1.0.0       -> 2.0.0-beta KO
                    // 1.0.0-alpha -> 2.0.0-beta KO
                    if (!currentVersion.IsPrerelease)
                        return false;

                    if ((version.Major, version.Minor, version.Patch) == (currentVersion.Major, currentVersion.Minor, currentVersion.Patch))
                        return true;
                }

                return true;
            }

            var index = await s_httpClient.GetFromJsonAsync<DotNetReleaseIndex>("https://raw.githubusercontent.com/dotnet/core/main/release-notes/releases-index.json", cancellationToken);
            var versions = index!.Releases!.Select(r => SemanticVersion.Parse(r.LastestSdk));

            var latestVersion = versions.Where(ConsiderVersion).DefaultIfEmpty().Max();
            if (latestVersion == null)
                return null;

            await dependency.UpdateAsync(latestVersion.ToString(), cancellationToken);
            return latestVersion.ToString();
        }

        private static async Task<string?> UpdateNpmDependencyAsync(Dependency dependency, CancellationToken cancellationToken)
        {
            var version = dependency.Version;
            if (version.Length > 0 && version[0] is '~' or '^')
            {
                version = version[1..];
            }

            if (!SemanticVersion.TryParse(version, out var currentVersion))
                return null;

            bool ConsiderVersion(SemanticVersion version)
            {
                // Only consider newer version
                if (version <= currentVersion)
                    return false;

                // Stable version are always considered
                if (version.IsPrerelease)
                {
                    // 1.0.0-alpha -> 1.0.0-beta OK
                    // 1.0.0-alpha -> 2.0.0      OK
                    // 1.0.0       -> 2.0.0-beta KO
                    // 1.0.0-alpha -> 2.0.0-beta KO
                    if (!currentVersion.IsPrerelease)
                        return false;

                    if ((version.Major, version.Minor, version.Patch) == (currentVersion.Major, currentVersion.Minor, currentVersion.Patch))
                        return true;
                }

                return true;
            }

            var index = await s_httpClient.GetFromJsonAsync<NpmPackageInfo>("https://registry.npmjs.org/" + dependency.Name, cancellationToken);
            var versions = new[] { index!.DistTag!.Latest, index.DistTag.Next }.WhereNotNullOrWhiteSpace().Select(v => SemanticVersion.Parse(v));

            var latestVersion = versions.Where(ConsiderVersion).DefaultIfEmpty().Max();
            if (latestVersion == null)
                return null;

            await dependency.UpdateAsync(latestVersion.ToString(), cancellationToken);
            return latestVersion.ToString();
        }

        private sealed class NpmPackageInfo
        {
            [JsonPropertyName("dist-tags")]
            public NpmPackageDistTag? DistTag { get; set; }
        }

        private sealed class NpmPackageDistTag
        {
            [JsonPropertyName("latest")]
            public string? Latest { get; set; }

            [JsonPropertyName("next")]
            public string? Next { get; set; }
        }

        private sealed class DotNetReleaseIndex
        {
            [JsonPropertyName("releases-index")]
            public IReadOnlyCollection<DotNetReleaseEntry>? Releases { get; set; }
        }

        private sealed class DotNetReleaseEntry
        {
            [JsonPropertyName("channel-version")]
            public string? ChannelVersion { get; set; }

            [JsonPropertyName("latest-sdk")]
            public string? LastestSdk { get; set; }
        }
    }
}
