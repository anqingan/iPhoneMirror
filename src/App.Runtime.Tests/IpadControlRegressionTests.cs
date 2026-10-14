using System.Windows;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunIpadControlRegressions()
    {
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext());
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", KeyboardTestMembers)!.SetValue(app, true);
        app.InitializeComponent();
        try
        {
            TestFullScreenFocusReconciliation();
            TestFailedUsbCleanupBeforeRetry();
            TestFailedUsbCleanupBeforeRetry(cancel: true);
            Console.WriteLine("PASS iPad: fullscreen focus recovery and USB cleanup before retry (no device input).");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void TestFullScreenFocusReconciliation()
    {
        var constructor = typeof(NativePreviewWindow).GetConstructors(KeyboardTestMembers).Single();
        var args = constructor.GetParameters().Select(p => p.DefaultValue).ToArray();
        var attachments = 0;
        var detachments = 0;
        object[] required = [1668u, 2388u, "iPad fullscreen regression",
            (Func<nint, bool>)(_ => { attachments++; return true; }),
            (Action<nint>)(_ => detachments++), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
        Array.Copy(required, args, required.Length);
        using var preview = (NativePreviewWindow)constructor.Invoke(args);
        KeyboardCall(preview, "ShowInitially");
        nint foreground = preview.Handle;
        SetKeyboardField(preview, "_inputForegroundWindow", (Func<nint>)(() => foreground));
        var activations = new List<bool>();
        preview.ActivationChanged += activations.Add;
        var handle = preview.Handle;
        var attached = attachments;
        var detached = detachments;
        foreach (var expectedFullScreen in new[] { true, false, true, false })
        {
            // The OS may not emit WM_ACTIVATE for a window already active.
            // Discard native events and verify the deferred reconciliation.
            foreground = handle;
            preview.ToggleFullScreen();
            activations.Clear();
            DrainDispatcher();
            InteractionAssert(preview.IsFullScreen == expectedFullScreen && activations.LastOrDefault(),
                "Fullscreen transition did not restore the active preview input owner.");
            InteractionAssert(preview.Handle == handle && attachments == attached && detachments == detached,
                "Fullscreen transition must preserve the capture/renderer lifetime.");
        }
        preview.ToggleFullScreen();
        activations.Clear();
        foreground = (nint)123; // Another app wins focus before the callback.
        DrainDispatcher();
        InteractionAssert(activations.Count > 0 && !activations[^1],
            "Deferred fullscreen recovery borrowed another application's keyboard focus.");
        preview.ToggleFullScreen();
        preview.Dispose();
        activations.Clear();
        DrainDispatcher();
        InteractionAssert(activations.Count == 0, "Disposed preview published a stale focus callback.");
    }

    private static void TestFailedUsbCleanupBeforeRetry(bool cancel = false)
    {
        var vm = new MainViewModel();
        vm.SetupActive = true;
        var constructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var device = (DeviceViewModel)constructor.Invoke([
            "ipad-cleanup-test", "iPad Pro", "iPad16,6", "26.5", "USB", "",
            Enum.Parse(constructor.GetParameters()[6].ParameterType, "Ready")]);
        var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
        var host = new UsbTouchBridgeHost();
        control.WiredBridge = host;
        control.WiredTarget = device.Udid;
        control.Failed = true;
        control.ControlStatus.Begin(ControlStatusMode.Usb, device.Name);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetKeyboardField(host, "_stopTask", release.Task);
        var cleanup = (Task)KeyboardCall(vm, "CompleteFailedUsbBridgeAsync",
            control, host, device, "Test recovery exhausted", "test")!;
        try
        {
            InteractionAssert(control.Stopping && !cleanup.IsCompleted && control.WiredBridge is null,
                "Retry became available before the old bridge finished releasing sockets.");
            InteractionAssert(control.ControlStatus.Current?.Stage != ControlStage.Failed,
                "The retry error surface was published before cleanup completed.");
            var stop = control.StopOperation.RunAsync(_ => throw new InvalidOperationException("Duplicate cleanup"));
            InteractionAssert(!stop.IsCompleted, "Capture shutdown failed to join bridge cleanup.");
            if (cancel)
            {
                var cancelled = vm.CancelReverseControlAsync(ControlStatusMode.Usb, device.Udid);
                InteractionAssert(!cancelled.IsCompleted && control.WiredFailureCancelled,
                    "User cancellation was lost while joining failed bridge cleanup.");
            }
        }
        finally { release.TrySetResult(); }
        AwaitMapping(cleanup);
        InteractionAssert(!control.Stopping && control.ControlStatus.Current?.Stage ==
            (cancel ? ControlStage.Cancelled : ControlStage.Failed),
            "Retry was not restored after bridge cleanup completed.");
        if (cancel) InteractionAssert(!control.Failed, "Cancelled cleanup reopened a failure prompt.");
        AwaitMapping(vm.ShutdownAsync());
    }
}
