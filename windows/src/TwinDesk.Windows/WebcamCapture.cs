using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.Foundation;

namespace TwinDesk;

public sealed record WebcamDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

// Created/initialized on the form's STA. No microphone device is ever initialized.
public sealed class WebcamCapture : IAsyncDisposable
{
    private MediaCapture? capture;
    private MediaFrameReader? reader;
    private bool? originalExposurePriority;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim encoding = new(1, 1);
    private readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<byte[]> frame;
    private readonly Action<string> failed;
    private readonly VideoMode mode;
    public int Width => mode.Width;
    public int Height => mode.Height;
    public double FramesPerSecond { get; private set; }
    public WebcamCapture(Action<byte[]> frame, Action<string> failed, VideoMode? mode = null)
    { this.frame = frame; this.failed = failed; this.mode = mode ?? VideoMode.Hd; }

    public static async Task<IReadOnlyList<WebcamDevice>> DevicesAsync() =>
        (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).Select(d => new WebcamDevice(d.Id, d.Name)).ToList();

    public async Task StartAsync(string deviceId)
    {
        stop.Token.ThrowIfCancellationRequested();
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var group = groups.FirstOrDefault(g => g.SourceInfos.Any(s => s.DeviceInformation?.Id == deviceId));
        stop.Token.ThrowIfCancellationRequested();
        capture = new MediaCapture();
        capture.Failed += CaptureFailed;
        await capture.InitializeAsync(new MediaCaptureInitializationSettings {
            VideoDeviceId = deviceId,
            SourceGroup = group,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl
        });
        stop.Token.ThrowIfCancellationRequested();
        var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color && s.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color && s.Info.MediaStreamType == MediaStreamType.VideoRecord)
            ?? throw new IOException("This camera does not provide color video frames.");
        // Never silently upscale. Request 720p30 or 1080p15; real delivered fps
        // can be lower than the advertised rate. This webcam's 1080p30 can stall.
        var format = source.SupportedFormats.Where(f => f.VideoFormat?.Width == mode.Width && f.VideoFormat.Height == mode.Height &&
                f.FrameRate.Denominator > 0 && f.FrameRate.Numerator > 0 &&
                (double)f.FrameRate.Numerator / f.FrameRate.Denominator <= mode.Fps)
            .OrderBy(f => Math.Abs((double)f.FrameRate.Numerator / f.FrameRate.Denominator - mode.Fps))
            .ThenBy(f => f.Subtype == MediaEncodingSubtypes.Mjpg ? 0 : 1).FirstOrDefault()
            ?? throw new IOException($"This camera does not offer {mode.Width} × {mode.Height} at up to {mode.Fps} fps. Choose another video mode.");
        await source.SetFormatAsync(format);
        var exposurePriority = capture.VideoDeviceController.ExposurePriorityVideoControl;
        if (exposurePriority.Supported)
        {
            originalExposurePriority = exposurePriority.Enabled;
            exposurePriority.Enabled = false;
        }
        FramesPerSecond = (double)format.FrameRate.Numerator / format.FrameRate.Denominator;
        stop.Token.ThrowIfCancellationRequested();
        reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, new BitmapSize { Width = (uint)mode.Width, Height = (uint)mode.Height });
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        reader.FrameArrived += FrameArrived;
        var result = await reader.StartAsync();
        if (result != MediaFrameReaderStartStatus.Success) throw new IOException("Camera could not start: " + result);
        // Some drivers report Success but never deliver a frame. Do not show
        // 'Sharing' until the complete encode path has produced real video.
        try { await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token); }
        catch (TimeoutException) { throw new IOException("The camera started but did not deliver video. Reconnect it and try again."); }
    }
    private void CaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        if (stop.IsCancellationRequested) return;
        Cancel();
        failed("Camera stopped. Check its connection and Windows camera permissions, then enable sharing again.");
    }
    private async void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (stop.IsCancellationRequested || !encoding.Wait(0)) return;
        try
        {
            if (stop.IsCancellationRequested) return;
            using var reference = sender.TryAcquireLatestFrame();
            using var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encoded,
                new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.8f, PropertyType.Single) });
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapTransform.ScaledWidth = (uint)mode.Width;
            encoder.BitmapTransform.ScaledHeight = (uint)mode.Height;
            encoder.IsThumbnailGenerated = false;
            await encoder.FlushAsync();
            if (stop.IsCancellationRequested || encoded.Size is 0 or > Wire.MaxCameraJpeg) return;
            encoded.Seek(0);
            using var input = new DataReader(encoded);
            var size = checked((uint)encoded.Size);
            if (await input.LoadAsync(size) != size) throw new IOException("Incomplete camera frame.");
            var jpeg = new byte[size];
            input.ReadBytes(jpeg);
            if (!stop.IsCancellationRequested) { frame(jpeg); firstFrame.TrySetResult(); }
        }
        catch (Exception e)
        {
            if (!stop.IsCancellationRequested)
            {
                Cancel();
                failed("Camera stopped: " + e.Message);
            }
        }
        finally { encoding.Release(); }
    }
    public void Cancel() => stop.Cancel();
    public async ValueTask DisposeAsync()
    {
        Cancel();
        if (reader is not null)
        {
            reader.FrameArrived -= FrameArrived;
            try { await reader.StopAsync(); } catch { /* Device removal must still release the capture object. */ }
        }
        await encoding.WaitAsync();
        try
        {
            reader?.Dispose(); reader = null;
            if (capture is not null)
            {
                if (originalExposurePriority is { } original)
                    try { capture.VideoDeviceController.ExposurePriorityVideoControl.Enabled = original; } catch { }
                capture.Failed -= CaptureFailed; capture.Dispose(); capture = null;
            }
        }
        finally { encoding.Release(); }
        // Keep cancellation and gate usable by an already-dispatched FrameArrived callback.
    }
}
