using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EEWTelop.Wpf.ViewModels;

namespace EEWTelop.Wpf;

internal partial class DesktopOverlayWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const int TransparentStyle = 0x00000020;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const double MaximumWidth = 1280;
    private const double AspectRatio = 16.0 / 9.0;

    internal DesktopOverlayWindow(OverlayViewModel overlay)
    {
        InitializeComponent();
        DataContext = overlay;
        PositionOnPrimaryScreen();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, ExtendedStyleIndex);
        int error = Marshal.GetLastPInvokeError();
        if (style == 0 && error != 0) throw new Win32Exception(error);

        // Windows sends mouse input through a layered window with WS_EX_TRANSPARENT.
        int previousStyle = SetWindowLong(handle, ExtendedStyleIndex,
            style | TransparentStyle | ToolWindowStyle | NoActivateStyle);
        error = Marshal.GetLastPInvokeError();
        if (previousStyle == 0 && error != 0) throw new Win32Exception(error);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        base.OnClosed(e);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.WorkArea)) return;
        if (Dispatcher.CheckAccess())
            PositionOnPrimaryScreen();
        else
            _ = Dispatcher.InvokeAsync(PositionOnPrimaryScreen);
    }

    private void PositionOnPrimaryScreen()
    {
        Rect workArea = SystemParameters.WorkArea;
        Width = Math.Min(MaximumWidth, Math.Min(workArea.Width, workArea.Height * AspectRatio));
        Height = Width / AspectRatio;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);
}
