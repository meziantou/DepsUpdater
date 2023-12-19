using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Meziantou.Framework;
using Meziantou.Framework.DependencyScanning;
using NuGet.Versioning;
using Xunit;

namespace DepsUpdater.Tests;

public sealed class FunctionalTests
{
    [Fact]
    public async Task UpdateNuGetPackage()
    {
        await using var tempDir = TemporaryDirectory.Create();

        var path = tempDir.CreateEmptyFile("test.csproj");
        await File.WriteAllTextAsync(path, """
            <Project>
                <ItemGroup>
                    <PackageReference Include="Meziantou.Framework" Version="1.0.0" />
                </ItemGroup>
            </Project>
            """);

        var result = await Program.Main(["update", "--directory", tempDir.FullPath]);
        Assert.Equal(0, result);

        var deps = await ScanDependencies(tempDir);
        Assert.True(SemanticVersion.Parse(deps[0].Version) > SemanticVersion.Parse("1.0.700"));
    }

    [Fact]
    public async Task FilterDependencyType()
    {
        await using var tempDir = TemporaryDirectory.Create();

        await File.WriteAllTextAsync(tempDir.CreateEmptyFile("a.csproj"), """
            <Project>
                <ItemGroup>
                    <PackageReference Include="Meziantou.Framework" Version="1.0.0" />
                </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(tempDir.CreateEmptyFile("package.json"), """
            {
            "dependencies": {
                "npm": "8.0.0"
              }
            }
            """);

        var result = await Program.Main(["update", "--directory", tempDir.FullPath, "--dependency-type", "Npm"]);
        Assert.Equal(0, result);

        var deps = await ScanDependencies(tempDir);
        Assert.Equal("1.0.0", deps[0].Version);
        Assert.True(SemanticVersion.Parse(deps[1].Version) > SemanticVersion.Parse("8.6.0"));
    }

    private static async Task<IReadOnlyList<Dependency>> ScanDependencies(TemporaryDirectory temporaryDirectory)
    {
        var deps = (await DependencyScanner.ScanDirectoryAsync(temporaryDirectory.FullPath, options: null)).ToList();
        return deps.OrderBy(dep => dep.VersionLocation!.FilePath, System.StringComparer.Ordinal).ToArray();
    }
}