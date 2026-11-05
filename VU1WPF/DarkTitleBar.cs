using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VU1WPF
{
    /// <summary>
    /// Makes the native Windows title bar of a dialog dark (Windows 10 20H1+ / Windows 11),
    /// so standard dialogs match the dark app theme.
    /// </summary>
    internal static class DarkTitleBar
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Call from the window constructor.</summary>
        public static void Apply(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    int enabled = 1;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));
                    }
                }
                catch
                {
                    // Older Windows: keep the default title bar
                }
            };
        }
    }
}
