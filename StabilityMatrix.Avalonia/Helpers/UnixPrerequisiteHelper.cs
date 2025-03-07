﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia.Controls;
using DynamicData;
using FluentAvalonia.UI.Controls;
using NLog;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Packages;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Python;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.Helpers;

[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
public class UnixPrerequisiteHelper : IPrerequisiteHelper
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IDownloadService downloadService;
    private readonly ISettingsManager settingsManager;
    private readonly IPyRunner pyRunner;

    private DirectoryPath HomeDir => settingsManager.LibraryDir;
    private DirectoryPath AssetsDir => HomeDir.JoinDir("Assets");

    private DirectoryPath PythonDir => AssetsDir.JoinDir("Python310");
    public bool IsPythonInstalled => PythonDir.JoinFile(PyRunner.RelativePythonDllPath).Exists;
    private DirectoryPath PortableGitInstallDir => HomeDir + "PortableGit";
    public string GitBinPath => PortableGitInstallDir + "bin";

    private DirectoryPath NodeDir => AssetsDir.JoinDir("nodejs");
    private string NpmPath => Path.Combine(NodeDir, "bin", "npm");
    private bool IsNodeInstalled => File.Exists(NpmPath);

    // Cached store of whether or not git is installed
    private bool? isGitInstalled;

    public UnixPrerequisiteHelper(
        IDownloadService downloadService,
        ISettingsManager settingsManager,
        IPyRunner pyRunner
    )
    {
        this.downloadService = downloadService;
        this.settingsManager = settingsManager;
        this.pyRunner = pyRunner;
    }

    private async Task<bool> CheckIsGitInstalled()
    {
        var result = await ProcessRunner.RunBashCommand("git --version");
        isGitInstalled = result.ExitCode == 0;
        return isGitInstalled == true;
    }

    public Task InstallPackageRequirements(BasePackage package, IProgress<ProgressReport>? progress = null) =>
        InstallPackageRequirements(package.Prerequisites.ToList(), progress);

    public async Task InstallPackageRequirements(
        List<PackagePrerequisite> prerequisites,
        IProgress<ProgressReport>? progress = null
    )
    {
        await UnpackResourcesIfNecessary(progress);

        if (prerequisites.Contains(PackagePrerequisite.Python310))
        {
            await InstallPythonIfNecessary(progress);
            await InstallVirtualenvIfNecessary(progress);
        }

        if (prerequisites.Contains(PackagePrerequisite.Git))
        {
            await InstallGitIfNecessary(progress);
        }

        if (prerequisites.Contains(PackagePrerequisite.Node))
        {
            await InstallNodeIfNecessary(progress);
        }
    }

    private async Task InstallVirtualenvIfNecessary(IProgress<ProgressReport>? progress = null)
    {
        // python stuff
        if (!PyRunner.PipInstalled || !PyRunner.VenvInstalled)
        {
            progress?.Report(
                new ProgressReport(-1f, "Installing Python prerequisites...", isIndeterminate: true)
            );

            await pyRunner.Initialize().ConfigureAwait(false);

            if (!PyRunner.PipInstalled)
            {
                await pyRunner.SetupPip().ConfigureAwait(false);
            }
            if (!PyRunner.VenvInstalled)
            {
                await pyRunner.InstallPackage("virtualenv").ConfigureAwait(false);
            }
        }
    }

    public async Task InstallAllIfNecessary(IProgress<ProgressReport>? progress = null)
    {
        await UnpackResourcesIfNecessary(progress);
        await InstallPythonIfNecessary(progress);
    }

    public async Task UnpackResourcesIfNecessary(IProgress<ProgressReport>? progress = null)
    {
        // Array of (asset_uri, extract_to)
        var assets = new[] { (Assets.SevenZipExecutable, AssetsDir), (Assets.SevenZipLicense, AssetsDir), };

        progress?.Report(new ProgressReport(0, message: "Unpacking resources", isIndeterminate: true));

        Directory.CreateDirectory(AssetsDir);
        foreach (var (asset, extractDir) in assets)
        {
            await asset.ExtractToDir(extractDir);
        }

        progress?.Report(new ProgressReport(1, message: "Unpacking resources", isIndeterminate: false));
    }

    public async Task InstallGitIfNecessary(IProgress<ProgressReport>? progress = null)
    {
        if (isGitInstalled == true || (isGitInstalled == null && await CheckIsGitInstalled()))
            return;

        // Show prompt to install git
        var dialog = new ContentDialog
        {
            Title = "Git not found",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "The current operation requires Git. Please install it to continue."
                    },
                    new SelectableTextBlock { Text = "$ sudo apt install git" },
                }
            },
            PrimaryButtonText = Resources.Action_Retry,
            CloseButtonText = Resources.Action_Close,
            DefaultButton = ContentDialogButton.Primary,
        };

        while (true)
        {
            // Return if installed
            if (await CheckIsGitInstalled())
                return;
            if (await dialog.ShowAsync() == ContentDialogResult.None)
            {
                // Cancel
                throw new OperationCanceledException("Git installation canceled");
            }
            // Otherwise continue to retry indefinitely
        }
    }

    /// <inheritdoc />
    public Task RunGit(
        ProcessArgs args,
        Action<ProcessOutput>? onProcessOutput = null,
        string? workingDirectory = null
    )
    {
        // Async progress not supported on Unix
        return RunGit(args, workingDirectory);
    }

    /// <inheritdoc />
    public async Task RunGit(ProcessArgs args, string? workingDirectory = null)
    {
        var command = args.Prepend("git");

        var result = await ProcessRunner.RunBashCommand(command.ToArray(), workingDirectory ?? "");
        if (result.ExitCode != 0)
        {
            Logger.Error(
                "Git command [{Command}] failed with exit code " + "{ExitCode}:\n{StdOut}\n{StdErr}",
                command,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError
            );

            throw new ProcessException(
                $"Git command [{command}] failed with exit code"
                    + $" {result.ExitCode}:\n{result.StandardOutput}\n{result.StandardError}"
            );
        }
    }

    public async Task InstallPythonIfNecessary(IProgress<ProgressReport>? progress = null)
    {
        if (IsPythonInstalled)
            return;

        Directory.CreateDirectory(AssetsDir);

        // Download
        var remote = Assets.PythonDownloadUrl;
        var url = remote.Url;
        var hashSha256 = remote.HashSha256;

        var fileName = Path.GetFileName(url.LocalPath);
        var downloadPath = Path.Combine(AssetsDir, fileName);
        Logger.Info($"Downloading Python from {url} to {downloadPath}");
        try
        {
            await downloadService.DownloadToFileAsync(url.ToString(), downloadPath, progress);

            // Verify hash
            var actualHash = await FileHash.GetSha256Async(downloadPath);
            Logger.Info($"Verifying Python hash: (expected: {hashSha256}, actual: {actualHash})");
            if (actualHash != hashSha256)
            {
                throw new Exception(
                    $"Python download hash mismatch: expected {hashSha256}, actual {actualHash}"
                );
            }

            // Extract
            Logger.Info($"Extracting Python Zip: {downloadPath} to {PythonDir}");
            if (PythonDir.Exists)
            {
                await PythonDir.DeleteAsync(true);
            }
            progress?.Report(new ProgressReport(0, "Installing Python", isIndeterminate: true));
            await ArchiveHelper.Extract7ZAuto(downloadPath, PythonDir);

            // For Unix, move the inner 'python' folder up to the root PythonDir
            if (Compat.IsUnix)
            {
                var innerPythonDir = PythonDir.JoinDir("python");
                if (!innerPythonDir.Exists)
                {
                    throw new Exception(
                        $"Python download did not contain expected inner 'python' folder: {innerPythonDir}"
                    );
                }

                foreach (var folder in Directory.EnumerateDirectories(innerPythonDir))
                {
                    var folderName = Path.GetFileName(folder);
                    var dest = Path.Combine(PythonDir, folderName);
                    Directory.Move(folder, dest);
                }
                Directory.Delete(innerPythonDir);
            }
        }
        finally
        {
            // Cleanup download file
            if (File.Exists(downloadPath))
            {
                File.Delete(downloadPath);
            }
        }

        // Initialize pyrunner and install virtualenv
        await pyRunner.Initialize();
        await pyRunner.InstallPackage("virtualenv");

        progress?.Report(new ProgressReport(1, "Installing Python", isIndeterminate: false));
    }

    public Task<ProcessResult> GetGitOutput(ProcessArgs args, string? workingDirectory = null)
    {
        return ProcessRunner.RunBashCommand(args.Prepend("git").ToArray(), workingDirectory ?? "");
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    public async Task RunNpm(
        ProcessArgs args,
        string? workingDirectory = null,
        Action<ProcessOutput>? onProcessOutput = null,
        IReadOnlyDictionary<string, string>? envVars = null
    )
    {
        var command = args.Prepend([NpmPath]);

        var result = await ProcessRunner.RunBashCommand(command.ToArray(), workingDirectory ?? "");
        if (result.ExitCode != 0)
        {
            Logger.Error(
                "npm command [{Command}] failed with exit code " + "{ExitCode}:\n{StdOut}\n{StdErr}",
                command,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError
            );

            throw new ProcessException(
                $"npm command [{command}] failed with exit code"
                    + $" {result.ExitCode}:\n{result.StandardOutput}\n{result.StandardError}"
            );
        }

        onProcessOutput?.Invoke(ProcessOutput.FromStdOutLine(result.StandardOutput));
        onProcessOutput?.Invoke(ProcessOutput.FromStdErrLine(result.StandardError));
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    public async Task InstallNodeIfNecessary(IProgress<ProgressReport>? progress = null)
    {
        if (IsNodeInstalled)
        {
            Logger.Info("node already installed");
            return;
        }

        Logger.Info("Downloading node");

        var downloadUrl = Compat.IsMacOS
            ? "https://nodejs.org/dist/v20.11.0/node-v20.11.0-darwin-arm64.tar.gz"
            : "https://nodejs.org/dist/v20.11.0/node-v20.11.0-linux-x64.tar.gz";

        var nodeDownloadPath = AssetsDir.JoinFile(Path.GetFileName(downloadUrl));

        await downloadService.DownloadToFileAsync(downloadUrl, nodeDownloadPath, progress: progress);

        Logger.Info("Installing node");
        progress?.Report(
            new ProgressReport(
                progress: 0.5f,
                isIndeterminate: true,
                type: ProgressType.Generic,
                message: "Installing prerequisites..."
            )
        );

        // unzip
        await ArchiveHelper.Extract7ZAuto(nodeDownloadPath, AssetsDir);

        var nodeDir = Compat.IsMacOS
            ? AssetsDir.JoinDir("node-v20.11.0-darwin-arm64")
            : AssetsDir.JoinDir("node-v20.11.0-linux-x64");
        Directory.Move(nodeDir, NodeDir);

        progress?.Report(
            new ProgressReport(progress: 1f, message: "Node install complete", type: ProgressType.Generic)
        );

        File.Delete(nodeDownloadPath);
    }

    [UnsupportedOSPlatform("Linux")]
    [UnsupportedOSPlatform("macOS")]
    public Task InstallTkinterIfNecessary(IProgress<ProgressReport>? progress = null)
    {
        throw new PlatformNotSupportedException();
    }

    [UnsupportedOSPlatform("Linux")]
    [UnsupportedOSPlatform("macOS")]
    public Task InstallVcRedistIfNecessary(IProgress<ProgressReport>? progress = null)
    {
        throw new PlatformNotSupportedException();
    }
}
