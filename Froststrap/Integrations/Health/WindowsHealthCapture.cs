// SPDX-FileCopyrightText: 2026 Froststrap
// SPDX-FileCopyrightText: 2026 Merostrap
//
// SPDX-License-Identifier: MPL-2.0

// Ported from Merostrap (MPL-2.0). Foreground check inlined; no WindowsMacroInput dependency.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Froststrap.Integrations.Health;

/// <summary>Copies only the calibrated rectangle into memory. Nothing is saved to disk.</summary>
public sealed class WindowsHealthCapture
{
    public HealthPixels Capture(HealthRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        region.Validate();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Health capture requires Windows.");
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (region.X < left || region.Y < top || (long)region.X + region.Width > (long)left + width ||
            (long)region.Y + region.Height > (long)top + height)
            throw new ArgumentException("The selected health region is outside the current desktop.", nameof(region));
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot access the screen for health capture.");
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the health capture buffer.");
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = region.Width,
                    Height = -region.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot allocate the health capture pixels.");
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot select the health capture buffer.");
            if (!BitBlt(memory, 0, 0, region.Width, region.Height, screen, region.X, region.Y, 0x00CC0020 | 0x40000000))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Screen health capture failed.");
            // GDI can batch the copy. Finish drawing before reading the DIB's memory,
            // otherwise detection can receive stale/blank pixels.
            if (!GdiFlush())
                throw new Win32Exception("Cannot finish the screen health capture.");
            var pixels = new byte[checked(region.Width * region.Height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new(region.Width, region.Height, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static bool IsForegroundRoblox()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPixelsPerMeter, YPixelsPerMeter; public uint ColorsUsed, ImportantColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
}
