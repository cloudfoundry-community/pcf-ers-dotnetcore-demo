using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Build.Tasks;
using Nuke.Common;
using Nuke.Common.BuildServers;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.CloudFoundry;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Octokit;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.CloudFoundry.CloudFoundryTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>();

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    [Parameter("GitHub personal access token with access to the repo")]
    string GitHubToken;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion] readonly GitVersion GitVersion;
    [Parameter("Cloud Foundry Username")]
    readonly string CfUsername;
    [Parameter("Cloud Foundry Password")]
    readonly string CfPassword;
    [Parameter("Cloud Foundry Endpoint")]
    readonly string CfApiEndpoint;
    [Parameter("Cloud Foundry Org")]
    readonly string CfOrg;
    [Parameter("Cloud Foundry Space")]
    readonly string CfSpace;
    [Parameter("Number of apps (for distributed tracing)")]
    readonly int AppsCount = 3;

    [Parameter("Skip logging in Cloud Foundry and use the current logged in session")] 
    readonly bool CfSkipLogin;


    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ArtifactsDirectory => (AbsolutePath)TeamServices.Instance?.ArtifactStagingDirectory ??  
                                       (FileExists((AbsolutePath)"/tmp/garden-init") ? 
                                           RootDirectory / "../artifacts" : 
                                           RootDirectory / "artifacts");
    AbsolutePath PublishDirectory => RootDirectory / "src" / "bin" / Configuration / "netcoreapp2.2" / "publish";
    string PackageZipName => $"articulate-{GitVersion.MajorMinorPatch}.zip";
    
    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.GetNormalizedAssemblyVersion())
                .SetFileVersion(GitVersion.GetNormalizedFileVersion())
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .EnableNoRestore());
        });

    Target Publish => _ => _
        .Executes(() =>
        {
            Logger.Info(GitVersion == null);
            Logger.Info(GitVersion.NuGetVersionV2);
            DotNetPublish(s => s
                .SetProject(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.GetNormalizedAssemblyVersion())
                .SetFileVersion(GitVersion.GetNormalizedFileVersion())
                .SetInformationalVersion(GitVersion.InformationalVersion));
        });

    Target Pack => _ => _
        .DependsOn(Publish)
        .Executes(() =>
        {
            Directory.CreateDirectory(ArtifactsDirectory);
            DeleteFile(ArtifactsDirectory / PackageZipName);
            ZipFile.CreateFromDirectory(PublishDirectory, ArtifactsDirectory / PackageZipName);
            Logger.Block(ArtifactsDirectory / PackageZipName);
        });

    Target CfLogin => _ => _
        .OnlyWhenStatic(() => !CfSkipLogin)
        .Requires(() => CfUsername, () => CfPassword, () => CfApiEndpoint)
        .Unlisted()
        .Executes(() =>
        {
            CloudFoundryLogin(c => c
                .SetUsername(CfUsername)
                .SetPassword(CfPassword)
                .SetApiEndpoint(CfApiEndpoint)
                .SetOrg(CfOrg)
                .SetSpace(CfSpace));
        });
    
    Target DeployTask => _ => _
        .DependsOn(CfLogin)
        .After(Pack)
        .Requires(() => CfSpace, () => CfOrg)
        .Unlisted()
        .Description("Deploys to cloud Foundry without building. Meant to be used on build servers as part of stage")
        .Executes(async () =>
        {
            string appName = "ers1";
            
            var names = Enumerable.Range(1, AppsCount).Select(x => $"ers{x}").ToArray();;
            CloudFoundryCreateSpace(c => c
                .SetOrg(CfOrg)
                .SetSpace(CfSpace));
            CloudFoundryTarget(c => c
                .SetSpace(CfSpace)
                .SetOrg(CfOrg));
            CloudFoundryCreateService(c => c
                .SetService("p-service-registry")
                .SetPlan(CfApiEndpoint.Contains("api.run.pivotal.io") ? "trial" : "standard")
                .SetInstanceName("eureka"));
            CloudFoundryPush(c => c
                .SetRandomRoute(true)
                .SetPath(ArtifactsDirectory / PackageZipName)
                .CombineWith(names,(cs,v) => cs.SetAppName(v)), degreeOfParallelism: 1);
            await CloudFoundryEnsureServiceReady("eureka");
            CloudFoundryBindService(c => c
                .SetServiceInstance("eureka")
                .CombineWith(names,(cs,v) => cs.SetAppName(v)), degreeOfParallelism: 5);
            CloudFoundryRestart(c => c
                .SetAppName(appName)
                .CombineWith(names,(cs,v) => cs.SetAppName(v)), degreeOfParallelism: 5);
        });

    Target Deploy => _ => _
        .Triggers(Pack)
        .Description("Builds and deploys to cloud Foundry")
        .Triggers(DeployTask);
    
    Target Release => _ => _
        .Description("Creates a GitHub release (or ammends existing) and uploads buildpack artifact")
        .DependsOn(Publish)
        .Requires(() => GitHubToken)
        .Executes(async () =>
        {
            if (!GitRepository.IsGitHubRepository())
                ControlFlow.Fail("Only supported when git repo remote is github");
            if(!IsGitPushedToRemote)
                ControlFlow.Fail("Your local git repo has not been pushed to remote. Can't create release until source is upload");
            var client = new GitHubClient(new ProductHeaderValue("nuke-build"))
            {
                Credentials = new Credentials(GitHubToken, AuthenticationType.Bearer)
            };
            var gitIdParts = GitRepository.Identifier.Split("/");
            var owner = gitIdParts[0];
            var repoName = gitIdParts[1];
            
            var releaseName = $"v{GitVersion.MajorMinorPatch}";
            Release release;
            try
            {
                release = await client.Repository.Release.Get(owner, repoName, releaseName);
            }
            catch (NotFoundException)
            {
                var newRelease = new NewRelease(releaseName)
                {
                    Name = releaseName, 
                    Draft = false, 
                    Prerelease = false
                };
                release = await client.Repository.Release.Create(owner, repoName, newRelease);
            }

            var existingAsset = release.Assets.FirstOrDefault(x => x.Name == PackageZipName);
            if (existingAsset != null)
            {
                await client.Repository.Release.DeleteAsset(owner, repoName, existingAsset.Id);
            }
            
            var zipPackageLocation = ArtifactsDirectory / PackageZipName;
            var releaseAssetUpload = new ReleaseAssetUpload(PackageZipName, "application/zip", File.OpenRead(zipPackageLocation), null);
            var releaseAsset = await client.Repository.Release.UploadAsset(release, releaseAssetUpload);
            
            Logger.Block(releaseAsset.BrowserDownloadUrl);
        });
    
    
    
    bool IsGitPushedToRemote => GitTasks
        .Git("status")
        .Select(x => x.Text)
        .Count(x => x.Contains("nothing to commit, working tree clean") || x.StartsWith("Your branch is up to date with")) == 2;
}
