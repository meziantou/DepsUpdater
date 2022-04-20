using System;
using System.Collections.Concurrent;
using System.CommandLine;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Framework;
using Meziantou.Framework.DependencyScanning;
using Meziantou.Framework.Globbing;

[assembly: InternalsVisibleTo("DepsUpdater.Tests")]

namespace DepsUpdater;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand();
        AddUpdateCommand(rootCommand);
        return rootCommand.InvokeAsync(args);
    }

    private static void AddUpdateCommand(RootCommand rootCommand)
    {
        var updateCommand = new Command("update")
        {
            new Option<string>("--directory", description: "Root directory") { IsRequired = false },
            new Option<string[]>("--files", description: "Glob patterns to find files to update") { IsRequired = false },
            new Option<string[]>("--dependency-type", description: string.Join(", ", new [] { nameof(DependencyType.NuGet), nameof(DependencyType.DotNetSdk), nameof(DependencyType.Npm) }))
            {
                IsRequired = false,
            },
            new Option<bool>("--update-lock-files") { IsRequired = false },
        };

        updateCommand.Description = "Update dependencies";
        updateCommand.SetHandler((string? directory, string?[]? files, string?[]? dependencyType, bool updateLockFiles) => Update(directory, files, dependencyType, updateLockFiles), updateCommand.Options.ToArray());

        rootCommand.AddCommand(updateCommand);
    }

    private static async Task<int> Update(string? directory, string?[]? filePatterns, string?[]? dependencyType, bool updateLockFiles)
    {
        GlobCollection? globs = null;
        if (filePatterns != null && filePatterns.Length > 0)
        {
            globs = new(filePatterns.WhereNotNull().Select(pattern => Glob.Parse(pattern, GlobOptions.IgnoreCase)).ToArray());
        }
        else
        {
            globs = new(
                Glob.Parse("**/*", GlobOptions.None),
                Glob.Parse("!**/node_modules/**/*", GlobOptions.None),
                Glob.Parse("!**/.playwright/package/**/*", GlobOptions.None));
        }

        var dependencyTypes = dependencyType?.WhereNotNull().SelectMany(value => value.Split(new char[] { ',', ';', ' ' })).Select(Enum.Parse<DependencyType>).ToArray() ?? Array.Empty<DependencyType>();
        if (dependencyTypes.Length > 0)
        {
            Console.WriteLine("Updating: " + string.Join(",", dependencyTypes));
        }

        Console.WriteLine("Searching in:");
        foreach (var glob in globs)
        {
            Console.WriteLine("- " + glob.ToString());
        }

        var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, _) => cancellationTokenSource.Cancel();

        if (string.IsNullOrEmpty(directory))
        {
            directory = Environment.CurrentDirectory;
        }

        var options = new ScannerOptions()
        {
            RecurseSubdirectories = true,
            ShouldScanFilePredicate = globs.IsMatch,
            ShouldRecursePredicate = globs.IsPartialMatch,
        };

        var dependencies = await DependencyScanner.ScanDirectoryAsync(directory, options, cancellationTokenSource.Token).ToListAsync(cancellationTokenSource.Token);
        Console.WriteLine($"{dependencies.Count} dependencies found");
        foreach (var dependency in dependencies.OrderBy(dep => dep.Type).ThenBy(dep => dep.Name).ThenBy(dep => dep.Version))
        {
            Console.WriteLine("- " + dependency);
        }

        var updaters = new PackageUpdater[]
        {
            new NpmPackageUpdater(),
            new NuGetPackageUpdater(),
            new DotNetSdkUpdater(),
        };

        var updatedDependencies = new ConcurrentBag<Dependency>();
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationTokenSource.Token, MaxDegreeOfParallelism = 1 };
        await Parallel.ForEachAsync(dependencies.Where(d => d.Location.IsUpdatable), parallelOptions, async (dependency, cancellationToken) =>
        {
            if (dependencyTypes.Length > 0 && !dependencyTypes.Contains(dependency.Type))
                return;

            string? updatedVersion = null;
            foreach (var updater in updaters)
            {
                updatedVersion = await updater.UpdateAsync(dependency, cancellationTokenSource.Token);
                if (updatedVersion != null)
                    break;
            }

            if (updatedVersion != null)
            {
                Console.WriteLine($"Updated {dependency} -> {updatedVersion}");
                updatedDependencies.Add(dependency);
            }
        });

        if (updateLockFiles)
        {
            foreach (var updater in updaters)
            {
                await updater.UpdateLockFileAsync(FullPath.FromPath(directory), updatedDependencies, cancellationTokenSource.Token);
            }
        }

        return 0;
    }
}
