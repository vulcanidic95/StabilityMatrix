using KGySoft.CoreLibraries;
using NLog;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Processes;

namespace StabilityMatrix.Core.Models.Packages.Extensions;

public abstract class GitPackageExtensionManager(IPrerequisiteHelper prerequisiteHelper)
    : IPackageExtensionManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public abstract string RelativeInstallDirectory { get; }

    public virtual IEnumerable<ExtensionManifest> DefaultManifests { get; } =
        Enumerable.Empty<ExtensionManifest>();

    protected virtual IEnumerable<string> IndexRelativeDirectories => [RelativeInstallDirectory];

    public abstract Task<IEnumerable<PackageExtension>> GetManifestExtensionsAsync(
        ExtensionManifest manifest,
        CancellationToken cancellationToken = default
    );

    /// <inheritdoc />
    Task<IEnumerable<PackageExtension>> IPackageExtensionManager.GetManifestExtensionsAsync(
        ExtensionManifest manifest,
        CancellationToken cancellationToken
    )
    {
        return GetManifestExtensionsAsync(manifest, cancellationToken);
    }

    protected virtual IEnumerable<ExtensionManifest> GetManifests(InstalledPackage installedPackage)
    {
        if (installedPackage.ExtraExtensionManifestUrls is not { } customUrls)
        {
            return DefaultManifests;
        }

        var manifests = DefaultManifests.ToList();

        foreach (var url in customUrls)
        {
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                manifests.Add(new ExtensionManifest(uri));
            }
        }

        return manifests;
    }

    /// <inheritdoc />
    IEnumerable<ExtensionManifest> IPackageExtensionManager.GetManifests(InstalledPackage installedPackage)
    {
        return GetManifests(installedPackage);
    }

    /// <inheritdoc />
    public virtual async Task<IEnumerable<InstalledPackageExtension>> GetInstalledExtensionsAsync(
        InstalledPackage installedPackage,
        CancellationToken cancellationToken = default
    )
    {
        if (installedPackage.FullPath is not { } packagePath)
        {
            return Enumerable.Empty<InstalledPackageExtension>();
        }

        var extensions = new List<InstalledPackageExtension>();

        // Search for installed extensions in the package's index directories.
        foreach (
            var indexDirectory in IndexRelativeDirectories.Select(
                path => new DirectoryPath(packagePath, path)
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip directory if not exists
            if (!indexDirectory.Exists)
            {
                continue;
            }

            // Check subdirectories of the index directory
            foreach (var subDirectory in indexDirectory.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip if not valid git repository
                if (await prerequisiteHelper.CheckIsGitRepository(subDirectory).ConfigureAwait(false) != true)
                    continue;

                // Get git version
                var version = await prerequisiteHelper
                    .GetGitRepositoryVersion(subDirectory)
                    .ConfigureAwait(false);

                // Get git remote
                var remoteUrlResult = await prerequisiteHelper
                    .GetGitRepositoryRemoteOriginUrl(subDirectory)
                    .ConfigureAwait(false);

                extensions.Add(
                    new InstalledPackageExtension
                    {
                        Paths = [subDirectory],
                        Version = new PackageExtensionVersion
                        {
                            Tag = version.Tag,
                            Branch = version.Branch,
                            CommitSha = version.CommitSha
                        },
                        GitRepositoryUrl = remoteUrlResult.IsSuccessExitCode
                            ? remoteUrlResult.StandardOutput?.Trim()
                            : null
                    }
                );
            }
        }

        return extensions;
    }

    /// <inheritdoc />
    public virtual async Task InstallExtensionAsync(
        PackageExtension extension,
        InstalledPackage installedPackage,
        PackageExtensionVersion? version = null,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(installedPackage.FullPath);

        // Ensure type
        if (extension.InstallType?.ToLowerInvariant() != "git-clone")
        {
            throw new ArgumentException(
                $"Extension must have install type 'git-clone' but has '{extension.InstallType}'.",
                nameof(extension)
            );
        }

        // Git clone all files
        var cloneRoot = new DirectoryPath(installedPackage.FullPath, RelativeInstallDirectory);

        foreach (var repositoryUri in extension.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ProgressReport(0f, $"Cloning {repositoryUri}", isIndeterminate: true));

            await prerequisiteHelper
                .CloneGitRepository(cloneRoot, repositoryUri.ToString(), version)
                .ConfigureAwait(false);

            progress?.Report(new ProgressReport(1f, $"Cloned {repositoryUri}"));
        }
    }

    /// <inheritdoc />
    public virtual async Task UpdateExtensionAsync(
        InstalledPackageExtension installedExtension,
        InstalledPackage installedPackage,
        PackageExtensionVersion? version = null,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(installedPackage.FullPath);

        foreach (var repoPath in installedExtension.Paths.OfType<DirectoryPath>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check git
            if (!await prerequisiteHelper.CheckIsGitRepository(repoPath.FullPath).ConfigureAwait(false))
                continue;

            // Get remote url
            var remoteUrlResult = await prerequisiteHelper
                .GetGitRepositoryRemoteOriginUrl(repoPath.FullPath)
                .EnsureSuccessExitCode()
                .ConfigureAwait(false);

            progress?.Report(
                new ProgressReport(0f, $"Updating git repository {repoPath.Name}", isIndeterminate: true)
            );

            // If version not provided, use current branch
            if (version is null)
            {
                ArgumentNullException.ThrowIfNull(installedExtension.Version?.Branch);

                version = new PackageExtensionVersion { Branch = installedExtension.Version?.Branch };
            }

            await prerequisiteHelper
                .UpdateGitRepository(repoPath, remoteUrlResult.StandardOutput!.Trim(), version)
                .ConfigureAwait(false);

            progress?.Report(new ProgressReport(1f, $"Updated git repository {repoPath.Name}"));
        }
    }

    /// <inheritdoc />
    public virtual async Task UninstallExtensionAsync(
        InstalledPackageExtension installedExtension,
        InstalledPackage installedPackage,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var path in installedExtension.Paths.Where(p => p.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (path is DirectoryPath directoryPath)
            {
                await directoryPath
                    .DeleteVerboseAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await path.DeleteAsync().ConfigureAwait(false);
            }
        }
    }
}
