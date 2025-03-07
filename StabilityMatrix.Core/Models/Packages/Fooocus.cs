﻿using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Helper.Cache;
using StabilityMatrix.Core.Helper.HardwareInfo;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Python;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Core.Models.Packages;

[Singleton(typeof(BasePackage))]
public class Fooocus(
    IGithubApiCache githubApi,
    ISettingsManager settingsManager,
    IDownloadService downloadService,
    IPrerequisiteHelper prerequisiteHelper
) : BaseGitPackage(githubApi, settingsManager, downloadService, prerequisiteHelper)
{
    public override string Name => "Fooocus";
    public override string DisplayName { get; set; } = "Fooocus";
    public override string Author => "lllyasviel";

    public override string Blurb => "Fooocus is a rethinking of Stable Diffusion and Midjourney’s designs";

    public override string LicenseType => "GPL-3.0";
    public override string LicenseUrl => "https://github.com/lllyasviel/Fooocus/blob/main/LICENSE";
    public override string LaunchCommand => "launch.py";

    public override Uri PreviewImageUri =>
        new(
            "https://user-images.githubusercontent.com/19834515/261830306-f79c5981-cf80-4ee3-b06b-3fef3f8bfbc7.png"
        );

    public override List<LaunchOptionDefinition> LaunchOptions =>
        new()
        {
            new LaunchOptionDefinition
            {
                Name = "Preset",
                Type = LaunchOptionType.Bool,
                Options = { "--preset anime", "--preset realistic" }
            },
            new LaunchOptionDefinition
            {
                Name = "Port",
                Type = LaunchOptionType.String,
                Description = "Sets the listen port",
                Options = { "--port" }
            },
            new LaunchOptionDefinition
            {
                Name = "Share",
                Type = LaunchOptionType.Bool,
                Description = "Set whether to share on Gradio",
                Options = { "--share" }
            },
            new LaunchOptionDefinition
            {
                Name = "Listen",
                Type = LaunchOptionType.String,
                Description = "Set the listen interface",
                Options = { "--listen" }
            },
            new LaunchOptionDefinition
            {
                Name = "Output Directory",
                Type = LaunchOptionType.String,
                Description = "Override the output directory",
                Options = { "--output-directory" }
            },
            new LaunchOptionDefinition
            {
                Name = "Language",
                Type = LaunchOptionType.String,
                Description = "Change the language of the UI",
                Options = { "--language" }
            },
            new LaunchOptionDefinition
            {
                Name = "Auto-Launch",
                Type = LaunchOptionType.Bool,
                Options = { "--auto-launch" }
            },
            new LaunchOptionDefinition
            {
                Name = "Disable Image Log",
                Type = LaunchOptionType.Bool,
                Options = { "--disable-image-log" }
            },
            new LaunchOptionDefinition
            {
                Name = "Disable Analytics",
                Type = LaunchOptionType.Bool,
                Options = { "--disable-analytics" }
            },
            new LaunchOptionDefinition
            {
                Name = "Disable Preset Model Downloads",
                Type = LaunchOptionType.Bool,
                Options = { "--disable-preset-download" }
            },
            new LaunchOptionDefinition
            {
                Name = "Always Download Newer Models",
                Type = LaunchOptionType.Bool,
                Options = { "--always-download-new-model" }
            },
            new()
            {
                Name = "VRAM",
                Type = LaunchOptionType.Bool,
                InitialValue = HardwareHelper.IterGpuInfo().Select(gpu => gpu.MemoryLevel).Max() switch
                {
                    MemoryLevel.Low => "--always-low-vram",
                    MemoryLevel.Medium => "--always-normal-vram",
                    _ => null
                },
                Options =
                {
                    "--always-high-vram",
                    "--always-normal-vram",
                    "--always-low-vram",
                    "--always-no-vram"
                }
            },
            new LaunchOptionDefinition
            {
                Name = "Use DirectML",
                Type = LaunchOptionType.Bool,
                Description = "Use pytorch with DirectML support",
                InitialValue = HardwareHelper.PreferDirectML(),
                Options = { "--directml" }
            },
            new LaunchOptionDefinition
            {
                Name = "Disable Xformers",
                Type = LaunchOptionType.Bool,
                InitialValue = !HardwareHelper.HasNvidiaGpu(),
                Options = { "--disable-xformers" }
            },
            LaunchOptionDefinition.Extras
        };

    public override SharedFolderMethod RecommendedSharedFolderMethod => SharedFolderMethod.Configuration;

    public override IEnumerable<SharedFolderMethod> AvailableSharedFolderMethods =>
        new[] { SharedFolderMethod.Symlink, SharedFolderMethod.Configuration, SharedFolderMethod.None };

    public override Dictionary<SharedFolderType, IReadOnlyList<string>> SharedFolders =>
        new()
        {
            [SharedFolderType.StableDiffusion] = new[] { "models/checkpoints" },
            [SharedFolderType.Diffusers] = new[] { "models/diffusers" },
            [SharedFolderType.Lora] = new[] { "models/loras" },
            [SharedFolderType.CLIP] = new[] { "models/clip" },
            [SharedFolderType.TextualInversion] = new[] { "models/embeddings" },
            [SharedFolderType.VAE] = new[] { "models/vae" },
            [SharedFolderType.ApproxVAE] = new[] { "models/vae_approx" },
            [SharedFolderType.ControlNet] = new[] { "models/controlnet" },
            [SharedFolderType.GLIGEN] = new[] { "models/gligen" },
            [SharedFolderType.ESRGAN] = new[] { "models/upscale_models" },
            [SharedFolderType.Hypernetwork] = new[] { "models/hypernetworks" }
        };

    public override Dictionary<SharedOutputType, IReadOnlyList<string>>? SharedOutputFolders =>
        new() { [SharedOutputType.Text2Img] = new[] { "outputs" } };

    public override IEnumerable<TorchVersion> AvailableTorchVersions =>
        new[] { TorchVersion.Cpu, TorchVersion.Cuda, TorchVersion.DirectMl, TorchVersion.Rocm };

    public override string MainBranch => "main";

    public override bool ShouldIgnoreReleases => true;

    public override string OutputFolderName => "outputs";

    public override PackageDifficulty InstallerSortOrder => PackageDifficulty.Simple;

    public override async Task InstallPackage(
        string installLocation,
        TorchVersion torchVersion,
        SharedFolderMethod selectedSharedFolderMethod,
        DownloadPackageVersionOptions versionOptions,
        IProgress<ProgressReport>? progress = null,
        Action<ProcessOutput>? onConsoleOutput = null
    )
    {
        var venvRunner = await SetupVenv(installLocation, forceRecreate: true).ConfigureAwait(false);

        progress?.Report(new ProgressReport(-1f, "Installing requirements...", isIndeterminate: true));

        var pipArgs = new PipInstallArgs();

        if (torchVersion == TorchVersion.DirectMl)
        {
            pipArgs = pipArgs.WithTorchDirectML();
        }
        else
        {
            pipArgs = pipArgs
                .WithTorch("==2.1.0")
                .WithTorchVision("==0.16.0")
                .WithTorchExtraIndex(
                    torchVersion switch
                    {
                        TorchVersion.Cpu => "cpu",
                        TorchVersion.Cuda => "cu121",
                        TorchVersion.Rocm => "rocm5.6",
                        _ => throw new ArgumentOutOfRangeException(nameof(torchVersion), torchVersion, null)
                    }
                );
        }

        var requirements = new FilePath(installLocation, "requirements_versions.txt");

        pipArgs = pipArgs.WithParsedFromRequirementsTxt(
            await requirements.ReadAllTextAsync().ConfigureAwait(false),
            excludePattern: "torch"
        );

        await venvRunner.PipInstall(pipArgs, onConsoleOutput).ConfigureAwait(false);
    }

    public override async Task RunPackage(
        string installedPackagePath,
        string command,
        string arguments,
        Action<ProcessOutput>? onConsoleOutput
    )
    {
        await SetupVenv(installedPackagePath).ConfigureAwait(false);

        void HandleConsoleOutput(ProcessOutput s)
        {
            onConsoleOutput?.Invoke(s);

            if (s.Text.Contains("Use the app with", StringComparison.OrdinalIgnoreCase))
            {
                var regex = new Regex(@"(https?:\/\/)([^:\s]+):(\d+)");
                var match = regex.Match(s.Text);
                if (match.Success)
                {
                    WebUrl = match.Value;
                }
                OnStartupComplete(WebUrl);
            }
        }

        void HandleExit(int i)
        {
            Debug.WriteLine($"Venv process exited with code {i}");
            OnExit(i);
        }

        var args = $"\"{Path.Combine(installedPackagePath, command)}\" {arguments}";

        VenvRunner?.RunDetached(args.TrimEnd(), HandleConsoleOutput, HandleExit);
    }

    public override Task SetupModelFolders(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    )
    {
        return sharedFolderMethod switch
        {
            SharedFolderMethod.Symlink
                => base.SetupModelFolders(installDirectory, SharedFolderMethod.Symlink),
            SharedFolderMethod.Configuration => SetupModelFoldersConfig(installDirectory),
            SharedFolderMethod.None => Task.CompletedTask,
            _ => throw new ArgumentOutOfRangeException(nameof(sharedFolderMethod), sharedFolderMethod, null)
        };
    }

    public override Task RemoveModelFolderLinks(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    )
    {
        return sharedFolderMethod switch
        {
            SharedFolderMethod.Symlink => base.RemoveModelFolderLinks(installDirectory, sharedFolderMethod),
            SharedFolderMethod.Configuration => WriteDefaultConfig(installDirectory),
            SharedFolderMethod.None => Task.CompletedTask,
            _ => throw new ArgumentOutOfRangeException(nameof(sharedFolderMethod), sharedFolderMethod, null)
        };
    }

    private JsonSerializerOptions jsonSerializerOptions =
        new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    private async Task SetupModelFoldersConfig(DirectoryPath installDirectory)
    {
        var fooocusConfigPath = installDirectory.JoinFile("config.txt");

        var fooocusConfig = new JsonObject();

        if (fooocusConfigPath.Exists)
        {
            fooocusConfig =
                JsonSerializer.Deserialize<JsonObject>(
                    await fooocusConfigPath.ReadAllTextAsync().ConfigureAwait(false)
                ) ?? new JsonObject();
        }

        fooocusConfig["path_checkpoints"] = Path.Combine(settingsManager.ModelsDirectory, "StableDiffusion");
        fooocusConfig["path_loras"] = Path.Combine(settingsManager.ModelsDirectory, "Lora");
        fooocusConfig["path_embeddings"] = Path.Combine(settingsManager.ModelsDirectory, "TextualInversion");
        fooocusConfig["path_vae_approx"] = Path.Combine(settingsManager.ModelsDirectory, "ApproxVAE");
        fooocusConfig["path_upscale_models"] = Path.Combine(settingsManager.ModelsDirectory, "ESRGAN");
        fooocusConfig["path_inpaint"] = Path.Combine(installDirectory, "models", "inpaint");
        fooocusConfig["path_controlnet"] = Path.Combine(settingsManager.ModelsDirectory, "ControlNet");
        fooocusConfig["path_clip_vision"] = Path.Combine(settingsManager.ModelsDirectory, "CLIP");
        fooocusConfig["path_fooocus_expansion"] = Path.Combine(
            installDirectory,
            "models",
            "prompt_expansion",
            "fooocus_expansion"
        );

        var outputsPath = Path.Combine(installDirectory, OutputFolderName);

        // doesn't always exist on first install
        Directory.CreateDirectory(outputsPath);
        fooocusConfig["path_outputs"] = outputsPath;

        await fooocusConfigPath
            .WriteAllTextAsync(JsonSerializer.Serialize(fooocusConfig, jsonSerializerOptions))
            .ConfigureAwait(false);
    }

    private async Task WriteDefaultConfig(DirectoryPath installDirectory)
    {
        var fooocusConfigPath = installDirectory.JoinFile("config.txt");

        var fooocusConfig = new JsonObject();

        if (fooocusConfigPath.Exists)
        {
            fooocusConfig =
                JsonSerializer.Deserialize<JsonObject>(
                    await fooocusConfigPath.ReadAllTextAsync().ConfigureAwait(false)
                ) ?? new JsonObject();
        }

        fooocusConfig["path_checkpoints"] = Path.Combine(installDirectory, "models", "checkpoints");
        fooocusConfig["path_loras"] = Path.Combine(installDirectory, "models", "loras");
        fooocusConfig["path_embeddings"] = Path.Combine(installDirectory, "models", "embeddings");
        fooocusConfig["path_vae_approx"] = Path.Combine(installDirectory, "models", "vae_approx");
        fooocusConfig["path_upscale_models"] = Path.Combine(installDirectory, "models", "upscale_models");
        fooocusConfig["path_inpaint"] = Path.Combine(installDirectory, "models", "inpaint");
        fooocusConfig["path_controlnet"] = Path.Combine(installDirectory, "models", "controlnet");
        fooocusConfig["path_clip_vision"] = Path.Combine(installDirectory, "models", "clip_vision");
        fooocusConfig["path_fooocus_expansion"] = Path.Combine(
            installDirectory,
            "models",
            "prompt_expansion",
            "fooocus_expansion"
        );
        fooocusConfig["path_outputs"] = Path.Combine(installDirectory, OutputFolderName);

        await fooocusConfigPath
            .WriteAllTextAsync(JsonSerializer.Serialize(fooocusConfig, jsonSerializerOptions))
            .ConfigureAwait(false);
    }
}
