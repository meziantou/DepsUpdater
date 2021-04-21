using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Framework;
using Meziantou.Framework.DependencyScanning;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DepsUpdater
{
    internal static class Program
    {
        private static readonly HttpClient s_httpClient = new();

        private static async Task Main(string[] args)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (_, _) => cancellationTokenSource.Cancel();
            var directory = Environment.CurrentDirectory;
            if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
            {
                directory = args[0];
            }

            var options = new ScannerOptions()
            {
                RecurseSubdirectories = true,
            };

            var dependencies = await DependencyScanner.ScanDirectoryAsync(directory, options, cancellationTokenSource.Token).ToListAsync();
            foreach (var dependency in dependencies.Where(d => d.Location.IsUpdatable))
            {
                if (dependency.Type == DependencyType.NuGet)
                {
                    await UpdateNuGetDependencyAsync(dependency, cancellationTokenSource.Token);
                }
                else if (dependency.Type == DependencyType.DotNetSdk)
                {
                    await UpdateDotNetSDKDependencyAsync(dependency, cancellationTokenSource.Token);
                }
            }
        }

        private static async Task UpdateNuGetDependencyAsync(Dependency dependency, CancellationToken cancellationToken)
        {
            if (!NuGetVersion.TryParse(dependency.Version, out var currentVersion))
                return;

            // https://github.com/NuGet/Samples/tree/main/NuGetProtocolSamples
            var cache = new SourceCacheContext();
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
                return;

            await dependency.UpdateAsync(latestVersion.ToString(), cancellationToken);
        }

        private static async Task UpdateDotNetSDKDependencyAsync(Dependency dependency, CancellationToken cancellationToken)
        {
            if (!SemanticVersion.TryParse(dependency.Version, out var currentVersion))
                return;

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
            var versions = index.Releases.Select(r => SemanticVersion.Parse(r.LastestSdk));

            var latestVersion = versions.Where(ConsiderVersion).DefaultIfEmpty().Max();
            if (latestVersion == null)
                return;

            await dependency.UpdateAsync(latestVersion.ToString(), cancellationToken);
        }

        private sealed class DotNetReleaseIndex
        {
            [JsonPropertyName("releases-index")]
            public IReadOnlyCollection<DotNetReleaseEntry> Releases { get; set; }
        }

        private sealed class DotNetReleaseEntry
        {
            [JsonPropertyName("channel-version")]
            public string ChannelVersion { get; set; }

            [JsonPropertyName("latest-sdk")]
            public string LastestSdk { get; set; }
        }
    }
}
