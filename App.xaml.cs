using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace _3MFTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(IsSystemDarkMode());
        SystemEvents.UserPreferenceChanged += (s, ev) =>
        {
            if (ev.Category == UserPreferenceCategory.General)
                Dispatcher.Invoke(() => ApplyTheme(IsSystemDarkMode()));
        };
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }

    public void ApplyTheme(bool isDark)
    {
        var r = Current.Resources;
        if (isDark)
        {
            r["WindowBackgroundColor"] = Color.FromRgb(0x1E, 0x1E, 0x1E);
            r["ControlBackgroundColor"] = Color.FromRgb(0x2D, 0x2D, 0x30);
            r["ControlBorderColor"] = Color.FromRgb(0x3F, 0x3F, 0x46);
            r["TextColor"] = Color.FromRgb(0xF1, 0xF1, 0xF1);
            r["TextDimColor"] = Color.FromRgb(0x99, 0x99, 0x99);
            r["AccentColor"] = Color.FromRgb(0x00, 0x7A, 0xCC);
            r["AccentHoverColor"] = Color.FromRgb(0x1C, 0x97, 0xEA);
            r["ListBackgroundColor"] = Color.FromRgb(0x25, 0x25, 0x26);
        }
        else
        {
            r["WindowBackgroundColor"] = Color.FromRgb(0xF3, 0xF3, 0xF3);
            r["ControlBackgroundColor"] = Color.FromRgb(0xFF, 0xFF, 0xFF);
            r["ControlBorderColor"] = Color.FromRgb(0xCC, 0xCE, 0xDB);
            r["TextColor"] = Color.FromRgb(0x1E, 0x1E, 0x1E);
            r["TextDimColor"] = Color.FromRgb(0x6D, 0x6D, 0x6D);
            r["AccentColor"] = Color.FromRgb(0x00, 0x5A, 0x9E);
            r["AccentHoverColor"] = Color.FromRgb(0x00, 0x6C, 0xBE);
            r["ListBackgroundColor"] = Color.FromRgb(0xFF, 0xFF, 0xFF);
        }
        r["WindowBackgroundBrush"] = new SolidColorBrush((Color)r["WindowBackgroundColor"]);
        r["ControlBackgroundBrush"] = new SolidColorBrush((Color)r["ControlBackgroundColor"]);
        r["ControlBorderBrush"] = new SolidColorBrush((Color)r["ControlBorderColor"]);
        r["TextBrush"] = new SolidColorBrush((Color)r["TextColor"]);
        r["TextDimBrush"] = new SolidColorBrush((Color)r["TextDimColor"]);
        r["AccentBrush"] = new SolidColorBrush((Color)r["AccentColor"]);
        r["AccentHoverBrush"] = new SolidColorBrush((Color)r["AccentHoverColor"]);
        r["ListBackgroundBrush"] = new SolidColorBrush((Color)r["ListBackgroundColor"]);
    }

    public static void SetWindowDarkMode(Window window, bool dark)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrSize);
}
