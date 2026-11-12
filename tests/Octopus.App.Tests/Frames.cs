using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Octopus.App.Tests;

/// <summary>Drives the headless render loop and saves frames as PNG for the screenshot tests.</summary>
internal static class Frames
{
    /// <summary>Runs pending UI jobs and renders a few frames.</summary>
    public static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// Renders frames (about 60 per second of real time, as Arc's motion runs on the clock) until the condition holds;
    /// each frame is reported to <paramref name="onFrame"/>. False when it did not hold within the time.
    /// </summary>
    public static bool Until(Func<bool> done, Action? onFrame = null, int milliseconds = 4000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(16);
            Pump();
            onFrame?.Invoke();
            if (done())
            {
                return true;
            }
        }
        return false;
    }

    public static void Save(Window window, string path)
    {
        Pump();
        window.CaptureRenderedFrame()?.Save(path, PngBitmapEncoderOptions.Default);
    }
}
