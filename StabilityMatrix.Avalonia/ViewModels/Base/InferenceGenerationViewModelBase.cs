﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ExifLibrary;
using MetadataExtractor.Formats.Exif;
using NLog;
using Refit;
using SkiaSharp;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Helpers;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Avalonia.ViewModels.Inference;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Animation;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.WebSocketData;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;
using Notification = DesktopNotifications.Notification;

namespace StabilityMatrix.Avalonia.ViewModels.Base;

/// <summary>
/// Abstract base class for tab view models that generate images using ClientManager.
/// This includes a progress reporter, image output view model, and generation virtual methods.
/// </summary>
[SuppressMessage("ReSharper", "VirtualMemberNeverOverridden.Global")]
public abstract partial class InferenceGenerationViewModelBase
    : InferenceTabViewModelBase,
        IImageGalleryComponent
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ISettingsManager settingsManager;
    private readonly INotificationService notificationService;
    private readonly ServiceManager<ViewModelBase> vmFactory;

    [JsonPropertyName("ImageGallery")]
    public ImageGalleryCardViewModel ImageGalleryCardViewModel { get; }

    [JsonIgnore]
    public ImageFolderCardViewModel ImageFolderCardViewModel { get; }

    [JsonIgnore]
    public ProgressViewModel OutputProgress { get; } = new();

    [JsonIgnore]
    public IInferenceClientManager ClientManager { get; }

    /// <inheritdoc />
    protected InferenceGenerationViewModelBase(
        ServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager
    )
        : base(notificationService)
    {
        this.notificationService = notificationService;
        this.settingsManager = settingsManager;
        this.vmFactory = vmFactory;

        ClientManager = inferenceClientManager;

        ImageGalleryCardViewModel = vmFactory.Get<ImageGalleryCardViewModel>();
        ImageFolderCardViewModel = vmFactory.Get<ImageFolderCardViewModel>();

        GenerateImageCommand.WithConditionalNotificationErrorHandler(notificationService);
    }

    /// <summary>
    /// Write an image to the default output folder
    /// </summary>
    protected Task<FilePath> WriteOutputImageAsync(
        Stream imageStream,
        ImageGenerationEventArgs args,
        int batchNum = 0,
        int batchTotal = 0,
        bool isGrid = false,
        string fileExtension = "png"
    )
    {
        var defaultOutputDir = settingsManager.ImagesInferenceDirectory;
        defaultOutputDir.Create();

        return WriteOutputImageAsync(
            imageStream,
            defaultOutputDir,
            args,
            batchNum,
            batchTotal,
            isGrid,
            fileExtension
        );
    }

    /// <summary>
    /// Write an image to an output folder
    /// </summary>
    protected async Task<FilePath> WriteOutputImageAsync(
        Stream imageStream,
        DirectoryPath outputDir,
        ImageGenerationEventArgs args,
        int batchNum = 0,
        int batchTotal = 0,
        bool isGrid = false,
        string fileExtension = "png"
    )
    {
        var formatTemplateStr = settingsManager.Settings.InferenceOutputImageFileNameFormat;

        var formatProvider = new FileNameFormatProvider
        {
            GenerationParameters = args.Parameters,
            ProjectType = args.Project?.ProjectType,
            ProjectName = ProjectFile?.NameWithoutExtension
        };

        // Parse to format
        if (
            string.IsNullOrEmpty(formatTemplateStr)
            || !FileNameFormat.TryParse(formatTemplateStr, formatProvider, out var format)
        )
        {
            // Fallback to default
            Logger.Warn(
                "Failed to parse format template: {FormatTemplate}, using default",
                formatTemplateStr
            );

            format = FileNameFormat.Parse(FileNameFormat.DefaultTemplate, formatProvider);
        }

        if (isGrid)
        {
            format = format.WithGridPrefix();
        }

        if (batchNum >= 1 && batchTotal > 1)
        {
            format = format.WithBatchPostFix(batchNum, batchTotal);
        }

        var fileName = format.GetFileName();
        var file = outputDir.JoinFile($"{fileName}.{fileExtension}");

        // Until the file is free, keep adding _{i} to the end
        for (var i = 0; i < 100; i++)
        {
            if (!file.Exists)
                break;

            file = outputDir.JoinFile($"{fileName}_{i + 1}.{fileExtension}");
        }

        // If that fails, append an 7-char uuid
        if (file.Exists)
        {
            var uuid = Guid.NewGuid().ToString("N")[..7];
            file = outputDir.JoinFile($"{fileName}_{uuid}.{fileExtension}");
        }

        if (file.Info.DirectoryName != null)
        {
            Directory.CreateDirectory(file.Info.DirectoryName);
        }

        await using var fileStream = file.Info.OpenWrite();
        await imageStream.CopyToAsync(fileStream);

        return file;
    }

    /// <summary>
    /// Builds the image generation prompt
    /// </summary>
    protected virtual void BuildPrompt(BuildPromptEventArgs args) { }

    /// <summary>
    /// Gets ImageSources that need to be uploaded as inputs
    /// </summary>
    protected virtual IEnumerable<ImageSource> GetInputImages()
    {
        return Enumerable.Empty<ImageSource>();
    }

    protected async Task UploadInputImages(ComfyClient client)
    {
        foreach (var image in GetInputImages())
        {
            if (image.LocalFile is { } localFile)
            {
                var uploadName = await image.GetHashGuidFileNameAsync();

                Logger.Debug("Uploading image {FileName} as {UploadName}", localFile.Name, uploadName);

                // For pngs, strip metadata since Pillow can't handle some valid files?
                if (localFile.Info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = PngDataHelper.RemoveMetadata(await localFile.ReadAllBytesAsync());
                    using var stream = new MemoryStream(bytes);

                    await client.UploadImageAsync(stream, uploadName);
                }
                else
                {
                    await using var stream = localFile.Info.OpenRead();

                    await client.UploadImageAsync(stream, uploadName);
                }
            }
        }
    }

    /// <summary>
    /// Runs a generation task
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if args.Parameters or args.Project are null</exception>
    protected async Task RunGeneration(ImageGenerationEventArgs args, CancellationToken cancellationToken)
    {
        var client = args.Client;
        var nodes = args.Nodes;

        // Checks
        if (args.Parameters is null)
            throw new InvalidOperationException("Parameters is null");
        if (args.Project is null)
            throw new InvalidOperationException("Project is null");
        if (args.OutputNodeNames.Count == 0)
            throw new InvalidOperationException("OutputNodeNames is empty");
        if (client.OutputImagesDir is null)
            throw new InvalidOperationException("OutputImagesDir is null");

        // Upload input images
        await UploadInputImages(client);

        // Connect preview image handler
        client.PreviewImageReceived += OnPreviewImageReceived;

        // Register to interrupt if user cancels
        var promptInterrupt = cancellationToken.Register(() =>
        {
            Logger.Info("Cancelling prompt");
            client
                .InterruptPromptAsync(new CancellationTokenSource(5000).Token)
                .SafeFireAndForget(ex =>
                {
                    Logger.Warn(ex, "Error while interrupting prompt");
                });
        });

        ComfyTask? promptTask = null;

        try
        {
            var timer = Stopwatch.StartNew();

            try
            {
                promptTask = await client.QueuePromptAsync(nodes, cancellationToken);
            }
            catch (ApiException e)
            {
                Logger.Warn(e, "Api exception while queuing prompt");
                await DialogHelper.CreateApiExceptionDialog(e, "Api Error").ShowAsync();
                return;
            }

            // Register progress handler
            promptTask.ProgressUpdate += OnProgressUpdateReceived;

            // Delay attaching running node change handler to not show indeterminate progress
            // if progress updates are received before the prompt starts
            Task.Run(
                    async () =>
                    {
                        var delayTime = 250 - (int)timer.ElapsedMilliseconds;
                        if (delayTime > 0)
                        {
                            await Task.Delay(delayTime, cancellationToken);
                        }

                        // ReSharper disable once AccessToDisposedClosure
                        AttachRunningNodeChangedHandler(promptTask);
                    },
                    cancellationToken
                )
                .SafeFireAndForget();

            // Wait for prompt to finish
            try
            {
                await promptTask.Task.WaitAsync(cancellationToken);
                Logger.Debug($"Prompt task {promptTask.Id} finished");
            }
            catch (ComfyNodeException e)
            {
                Logger.Warn(e, "Comfy node exception while queuing prompt");
                await DialogHelper
                    .CreateJsonDialog(e.JsonData, "Comfy Error", "Node execution encountered an error")
                    .ShowAsync();
                return;
            }

            // Get output images
            var imageOutputs = await client.GetImagesForExecutedPromptAsync(promptTask.Id, cancellationToken);

            if (
                !imageOutputs.TryGetValue(args.OutputNodeNames[0], out var images)
                || images is not { Count: > 0 }
            )
            {
                // No images match
                notificationService.Show(
                    "No output",
                    "Did not receive any output images",
                    NotificationType.Warning
                );
                return;
            }

            // Disable cancellation
            await promptInterrupt.DisposeAsync();

            if (args.ClearOutputImages)
            {
                ImageGalleryCardViewModel.ImageSources.Clear();
            }

            var outputImages = await ProcessOutputImages(images, args);

            var notificationImage = outputImages.FirstOrDefault()?.LocalFile;

            await notificationService.ShowAsync(
                NotificationKey.Inference_PromptCompleted,
                new Notification
                {
                    Title = "Prompt Completed",
                    Body = $"Prompt [{promptTask.Id[..7].ToLower()}] completed successfully",
                    BodyImagePath = notificationImage?.FullPath
                }
            );
        }
        finally
        {
            // Disconnect progress handler
            client.PreviewImageReceived -= OnPreviewImageReceived;

            // Clear progress
            OutputProgress.ClearProgress();
            ImageGalleryCardViewModel.PreviewImage?.Dispose();
            ImageGalleryCardViewModel.PreviewImage = null;
            ImageGalleryCardViewModel.IsPreviewOverlayEnabled = false;

            // Cleanup tasks
            promptTask?.Dispose();
        }
    }

    /// <summary>
    /// Handles image output metadata for generation runs
    /// </summary>
    private async Task<List<ImageSource>> ProcessOutputImages(
        IReadOnlyCollection<ComfyImage> images,
        ImageGenerationEventArgs args
    )
    {
        var client = args.Client;

        // Write metadata to images
        var outputImagesBytes = new List<byte[]>();
        var outputImages = new List<ImageSource>();

        foreach (var (i, comfyImage) in images.Enumerate())
        {
            Logger.Debug("Downloading image: {FileName}", comfyImage.FileName);
            var imageStream = await client.GetImageStreamAsync(comfyImage);

            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms);

            var imageArray = ms.ToArray();
            outputImagesBytes.Add(imageArray);

            var parameters = args.Parameters!;
            var project = args.Project!;

            // Lock seed
            project.TryUpdateModel<SeedCardModel>("Seed", model => model with { IsRandomizeEnabled = false });

            // Seed and batch override for batches
            if (images.Count > 1 && project.ProjectType is InferenceProjectType.TextToImage)
            {
                project = (InferenceProjectDocument)project.Clone();

                // Set batch size indexes
                project.TryUpdateModel(
                    "BatchSize",
                    node =>
                    {
                        node[nameof(BatchSizeCardViewModel.BatchCount)] = 1;
                        node[nameof(BatchSizeCardViewModel.IsBatchIndexEnabled)] = true;
                        node[nameof(BatchSizeCardViewModel.BatchIndex)] = i + 1;
                        return node;
                    }
                );
            }

            if (comfyImage.FileName.EndsWith(".png"))
            {
                var bytesWithMetadata = PngDataHelper.AddMetadata(imageArray, parameters, project);

                // Write using generated name
                var filePath = await WriteOutputImageAsync(
                    new MemoryStream(bytesWithMetadata),
                    args,
                    i + 1,
                    images.Count
                );

                outputImages.Add(new ImageSource(filePath));
                EventManager.Instance.OnImageFileAdded(filePath);
            }
            else if (comfyImage.FileName.EndsWith(".webp"))
            {
                var opts = new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() }
                };
                var paramsJson = JsonSerializer.Serialize(parameters, opts);
                var smProject = JsonSerializer.Serialize(project, opts);
                var metadata = new Dictionary<ExifTag, string>
                {
                    { ExifTag.ImageDescription, paramsJson },
                    { ExifTag.Software, smProject }
                };

                var bytesWithMetadata = ImageMetadata.AddMetadataToWebp(imageArray, metadata);

                // Write using generated name
                var filePath = await WriteOutputImageAsync(
                    new MemoryStream(bytesWithMetadata.ToArray()),
                    args,
                    i + 1,
                    images.Count,
                    fileExtension: Path.GetExtension(comfyImage.FileName).Replace(".", "")
                );

                outputImages.Add(new ImageSource(filePath));
                EventManager.Instance.OnImageFileAdded(filePath);
            }
            else
            {
                // Write using generated name
                var filePath = await WriteOutputImageAsync(
                    new MemoryStream(imageArray),
                    args,
                    i + 1,
                    images.Count,
                    fileExtension: Path.GetExtension(comfyImage.FileName).Replace(".", "")
                );

                outputImages.Add(new ImageSource(filePath));
                EventManager.Instance.OnImageFileAdded(filePath);
            }
        }

        // Download all images to make grid, if multiple
        if (outputImages.Count > 1)
        {
            var loadedImages = outputImagesBytes.Select(SKImage.FromEncodedData).ToImmutableArray();

            var project = args.Project!;

            // Lock seed
            project.TryUpdateModel<SeedCardModel>("Seed", model => model with { IsRandomizeEnabled = false });

            var grid = ImageProcessor.CreateImageGrid(loadedImages);
            var gridBytes = grid.Encode().ToArray();
            var gridBytesWithMetadata = PngDataHelper.AddMetadata(gridBytes, args.Parameters!, args.Project!);

            // Save to disk
            var gridPath = await WriteOutputImageAsync(
                new MemoryStream(gridBytesWithMetadata),
                args,
                isGrid: true
            );

            // Insert to start of images
            var gridImage = new ImageSource(gridPath);
            outputImages.Insert(0, gridImage);
            EventManager.Instance.OnImageFileAdded(gridPath);
        }

        foreach (var img in outputImages)
        {
            // Preload
            await img.GetBitmapAsync();
            // Add images
            ImageGalleryCardViewModel.ImageSources.Add(img);
        }

        return outputImages;
    }

    /// <summary>
    /// Implementation for Generate Image
    /// </summary>
    protected virtual Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Command for the Generate Image button
    /// </summary>
    /// <param name="options">Optional overrides (side buttons)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [RelayCommand(IncludeCancelCommand = true, FlowExceptionsToTaskScheduler = true)]
    private async Task GenerateImage(
        GenerateFlags options = default,
        CancellationToken cancellationToken = default
    )
    {
        var overrides = GenerateOverrides.FromFlags(options);

        try
        {
            await GenerateImageImpl(overrides, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Logger.Debug($"Image Generation Canceled");
        }
    }

    /// <summary>
    /// Shows a prompt and return false if client not connected
    /// </summary>
    protected async Task<bool> CheckClientConnectedWithPrompt()
    {
        if (ClientManager.IsConnected)
            return true;

        var vm = vmFactory.Get<InferenceConnectionHelpViewModel>();
        await vm.CreateDialog().ShowAsync();

        return ClientManager.IsConnected;
    }

    /// <summary>
    /// Handles the preview image received event from the websocket.
    /// Updates the preview image in the image gallery.
    /// </summary>
    protected virtual void OnPreviewImageReceived(object? sender, ComfyWebSocketImageData args)
    {
        ImageGalleryCardViewModel.SetPreviewImage(args.ImageBytes);
    }

    /// <summary>
    /// Handles the progress update received event from the websocket.
    /// Updates the progress view model.
    /// </summary>
    protected virtual void OnProgressUpdateReceived(object? sender, ComfyProgressUpdateEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OutputProgress.Value = args.Value;
            OutputProgress.Maximum = args.Maximum;
            OutputProgress.IsIndeterminate = false;

            OutputProgress.Text =
                $"({args.Value} / {args.Maximum})" + (args.RunningNode != null ? $" {args.RunningNode}" : "");
        });
    }

    private void AttachRunningNodeChangedHandler(ComfyTask comfyTask)
    {
        // Do initial update
        if (comfyTask.RunningNodesHistory.TryPeek(out var lastNode))
        {
            OnRunningNodeChanged(comfyTask, lastNode);
        }

        comfyTask.RunningNodeChanged += OnRunningNodeChanged;
    }

    /// <summary>
    /// Handles the node executing updates received event from the websocket.
    /// </summary>
    protected virtual void OnRunningNodeChanged(object? sender, string? nodeName)
    {
        // Ignore if regular progress updates started
        if (sender is not ComfyTask { HasProgressUpdateStarted: false })
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            OutputProgress.IsIndeterminate = true;
            OutputProgress.Value = 100;
            OutputProgress.Maximum = 100;
            OutputProgress.Text = nodeName;
        });
    }

    public class ImageGenerationEventArgs : EventArgs
    {
        public required ComfyClient Client { get; init; }
        public required NodeDictionary Nodes { get; init; }
        public required IReadOnlyList<string> OutputNodeNames { get; init; }
        public GenerationParameters? Parameters { get; init; }
        public InferenceProjectDocument? Project { get; init; }
        public bool ClearOutputImages { get; init; } = true;
    }

    public class BuildPromptEventArgs : EventArgs
    {
        public ComfyNodeBuilder Builder { get; } = new();
        public GenerateOverrides Overrides { get; init; } = new();
        public long? SeedOverride { get; init; }

        public static implicit operator ModuleApplyStepEventArgs(BuildPromptEventArgs args)
        {
            var overrides = new Dictionary<Type, bool>();

            if (args.Overrides.IsHiresFixEnabled.HasValue)
            {
                overrides[typeof(HiresFixModule)] = args.Overrides.IsHiresFixEnabled.Value;
            }

            return new ModuleApplyStepEventArgs { Builder = args.Builder, IsEnabledOverrides = overrides };
        }
    }
}
