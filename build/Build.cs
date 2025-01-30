using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.PathConstruction;

class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Pack);

    private const string LibraryProjectName = "Universley.OrleansContrib.StreamsProvider.Redis";

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;

    AbsolutePath SourceDirectory => RootDirectory;
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath NuGetPackagesDirectory => ArtifactsDirectory / "nuget";
    AbsolutePath TestResultDirectory => ArtifactsDirectory / "test-results";

    // Nuget API key can be also set as an environment variable
    [Parameter("NuGet API key")]
    readonly string NuGetApiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY");

    [Parameter("Build number override")]
    readonly string BuildNumber
        = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ?? $"{0}";

    [Parameter("Gitea Nuget package source name")]
    readonly string GiteaNugetSourceName = Environment.GetEnvironmentVariable("NUGET_SOURCE_NAME");

    Target Clean => _ => _
        .Before(Compile)
        .Executes(() =>
        {
            SourceDirectory
                .GlobDirectories("**/bin", "**/obj")
                .Except(RootDirectory.GlobDirectories("build/**/bin", "build/**/obj"))
                .ForEach(ap => Directory.Delete(ap, true));
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Compile => _ => _
        .Executes(() =>
        {
            var semVer = Version;
            var semFileVer = Version;
            var informationalVersion = Version;

            Log.Logger.Information("AssemblySemVer: {semVer}", semVer);

            DotNetTasks.DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(semVer)
                .SetFileVersion(semFileVer)
                .SetInformationalVersion(informationalVersion));
        });

    Target Test => _ => _
       .DependsOn(Compile)
       .Executes(() =>
       {
           DotNetTasks.DotNetTest(s => s
                   .SetProjectFile(Solution)
                   .SetConfiguration(Configuration)
                   .SetLoggers($"trx;LogFileName={TestResultDirectory / "testresults.trx"}")
           );
       });

    Target Pack => _ => _
        .DependsOn(Test)
        .Executes(() =>
        {

            DotNetTasks.DotNetPack(s => s
                    .SetProject(SourceDirectory / "Universley.OrleansContrib.StreamsProvider.Redis" / $"{LibraryProjectName}.csproj")
                    .SetConfiguration(Configuration)
                    .SetOutputDirectory(NuGetPackagesDirectory)
                    .EnableIncludeSymbols()
                    .SetSymbolPackageFormat(DotNetSymbolPackageFormat.snupkg)
                    .SetVersion(Version)
                    .SetNoBuild(true)
                 );
        });

    Target Publish => _ => _
            .DependsOn(Pack, CreateAndPushGitTag)
            .Executes(() =>
            {
                DotNetTasks.DotNetNuGetPush(s => s
                    .SetSource($"{GiteaNugetSourceName}")
                    .SetApiKey(NuGetApiKey)
                    .SetTargetPath(NuGetPackagesDirectory / $"{LibraryProjectName}.{Version}.snupkg"));

                DotNetTasks.DotNetNuGetPush(s => s
                     .SetSource($"{GiteaNugetSourceName}")
                     .SetApiKey(NuGetApiKey)
                     .SetTargetPath(NuGetPackagesDirectory / $"{LibraryProjectName}.{Version}.nupkg"));
            });

    Target CreateAndPushGitTag => _ => _
        .Executes(() =>
        {
            var gitTag = $"{Version}";
            GitTasks.Git($"tag {gitTag}");
            GitTasks.Git($"push origin {gitTag}");
        });

    private string Version
    {
        get
        {
            if (version == null)
            {
                version = GetVersion();
            }
            return version;
        }
    }

    private string version = null;

    private string GetVersion()
    {
        Log.Information("GitHub run number is {number}", Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER"));
        var buildNumber = BuildNumber;
        if (string.IsNullOrEmpty(buildNumber))
        {
            return "1.0.0";
        }

        var currentDateTime = DateTimeOffset.UtcNow;

        var assembledVersion = $"{currentDateTime.Year}.{currentDateTime.Month}.{buildNumber}";
        Log.Information("Assembled version: {assembledVersion}", assembledVersion);
        return assembledVersion;
    }
}
