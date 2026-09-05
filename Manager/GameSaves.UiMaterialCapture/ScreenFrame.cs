using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace GameSaves.UiMaterialCapture
{
    // A window material is composited by Windows, not drawn by the app, so
    // the only honest evidence is a read-back of the composited screen. The
    // app's own render (Avalonia's CaptureRenderedFrame) never contains the
    // Acrylic or Mica backdrop and would report success for a denied
    // material.
    internal sealed class ScreenFrame
    {
        private const uint SRCCOPY = 0x00CC0020;
        private const uint CAPTUREBLT = 0x40000000;
        private const uint DIB_RGB_COLORS = 0;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        private ScreenFrame(PixelRect bounds, byte[] pixels)
        {
            Bounds = bounds;
            Pixels = pixels;
        }

        // Screen coordinates in physical pixels, matching Avalonia's
        // PointToScreen, so element rectangles and frames share one space.
        public PixelRect Bounds { get; }

        // Top-down BGRA, stride = Bounds.Width * 4. Alpha is forced opaque:
        // BitBlt leaves the alpha channel of a screen copy undefined.
        public byte[] Pixels { get; }

        public static ScreenFrame CaptureWindow(Window window)
        {
            PixelRect bounds = FrameBounds(window);

            // Refusing is the only safe answer: a read-back returns whatever
            // is on the screen, so anything in front of this window would be
            // saved as if it were the application.
            if (!IsOwnedByThisProcess(bounds))
            {
                throw new InvalidOperationException(
                    $"Another application is in front of {bounds}. A screen " +
                    "read-back would capture it instead of this window, so the " +
                    "capture was refused. Run this harness on a session nobody " +
                    "is using.");
            }

            return Capture(bounds);
        }

        /// <summary>
        /// The window's composited rectangle in screen pixels: DWM's extended
        /// frame bounds, which is what a person sees, rather than the larger
        /// rectangle Win32 reports for the invisible resize border.
        /// </summary>
        public static PixelRect FrameBounds(Window window)
        {
            IntPtr handle = window.TryGetPlatformHandle()?.Handle
                ?? throw new InvalidOperationException(
                    "The window has no platform handle; it is not shown.");

            if (DwmGetWindowAttribute(
                    handle,
                    DWMWA_EXTENDED_FRAME_BOUNDS,
                    out RECT frame,
                    Marshal.SizeOf<RECT>()) != 0 &&
                !GetWindowRect(handle, out frame))
            {
                throw new InvalidOperationException(
                    "Could not read the window rectangle.");
            }

            return new PixelRect(
                frame.Left,
                frame.Top,
                Math.Max(1, frame.Right - frame.Left),
                Math.Max(1, frame.Bottom - frame.Top));
        }

        public static ScreenFrame Capture(PixelRect rect)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;

            try
            {
                memoryDc = CreateCompatibleDC(screenDc);
                bitmap = CreateCompatibleBitmap(screenDc, rect.Width, rect.Height);
                IntPtr previous = SelectObject(memoryDc, bitmap);

                if (!BitBlt(
                        memoryDc, 0, 0, rect.Width, rect.Height,
                        screenDc, rect.X, rect.Y, SRCCOPY | CAPTUREBLT))
                {
                    throw new InvalidOperationException(
                        $"BitBlt failed for {rect}: error " +
                        Marshal.GetLastWin32Error() +
                        $", screenDc={screenDc}, memoryDc={memoryDc}, bitmap={bitmap}");
                }

                SelectObject(memoryDc, previous);

                var header = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = rect.Width,
                    // Negative height requests a top-down image.
                    biHeight = -rect.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                };

                byte[] pixels = new byte[rect.Width * rect.Height * 4];

                if (GetDIBits(
                        screenDc, bitmap, 0, (uint)rect.Height,
                        pixels, ref header, DIB_RGB_COLORS) == 0)
                {
                    throw new InvalidOperationException("GetDIBits failed.");
                }

                for (int index = 3; index < pixels.Length; index += 4)
                    pixels[index] = 255;

                return new ScreenFrame(rect, pixels);
            }
            finally
            {
                if (bitmap != IntPtr.Zero)
                    DeleteObject(bitmap);
                if (memoryDc != IntPtr.Zero)
                    DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // The bitmap owns an unmanaged Skia surface. Leaving it to the
        // finalizer was survivable for a few dozen captures and killed the
        // process with an access violation inside the PNG encoder partway
        // through a gallery run, which writes hundreds.
        public void Save(string path)
        {
            using var bitmap = new WriteableBitmap(
                new PixelSize(Bounds.Width, Bounds.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            using (ILockedFramebuffer buffer = bitmap.Lock())
            {
                int rowBytes = Bounds.Width * 4;

                for (int row = 0; row < Bounds.Height; row++)
                {
                    Marshal.Copy(
                        Pixels,
                        row * rowBytes,
                        buffer.Address + (row * buffer.RowBytes),
                        rowBytes);
                }
            }

            bitmap.Save(path, new PngBitmapEncoderOptions());
        }

        // Mean per-channel difference and the share of clearly different
        // pixels inside one screen rectangle. Two captures of an opaque
        // surface over different backgrounds differ by 0; anything the OS
        // composites through the window moves both numbers.
        public RegionDifference Difference(ScreenFrame other, PixelRect region)
        {
            PixelRect area = region.Intersect(Bounds).Intersect(other.Bounds);

            if (area.Width <= 0 || area.Height <= 0)
                return RegionDifference.Empty;

            long total = 0;
            long changed = 0;
            long counted = 0;

            for (int y = area.Y; y < area.Y + area.Height; y++)
            {
                int mine = (((y - Bounds.Y) * Bounds.Width) + (area.X - Bounds.X)) * 4;
                int theirs =
                    (((y - other.Bounds.Y) * other.Bounds.Width) +
                        (area.X - other.Bounds.X)) * 4;

                for (int x = 0; x < area.Width; x++)
                {
                    int worst = 0;

                    for (int channel = 0; channel < 3; channel++)
                    {
                        int delta = Math.Abs(
                            Pixels[mine + channel] - other.Pixels[theirs + channel]);
                        total += delta;
                        worst = Math.Max(worst, delta);
                    }

                    if (worst > 8)
                        changed++;

                    counted++;
                    mine += 4;
                    theirs += 4;
                }
            }

            return new RegionDifference(
                total / (double)(counted * 3),
                changed / (double)counted,
                counted);
        }

        // An element's rectangle in the same screen pixels a frame stores.
        public static PixelRect? RegionOf(Visual? element, Window window)
        {
            if (element is null || !element.IsVisible)
                return null;

            if (element.TranslatePoint(default, window) is not { } topLeft)
                return null;

            Size size = element.Bounds.Size;

            if (size.Width < 4 || size.Height < 4)
                return null;

            double scale = window.RenderScaling;
            PixelPoint origin = window.PointToScreen(topLeft);

            return new PixelRect(
                origin.X,
                origin.Y,
                (int)Math.Round(size.Width * scale),
                (int)Math.Round(size.Height * scale));
        }

        /// <summary>
        /// Whether every part of a screen rectangle currently belongs to a
        /// window of this process.
        ///
        /// A screen read-back photographs whatever is on the screen, not
        /// whatever the harness believes is there. If any other application is
        /// in front of the capture area — a browser, a chat window, a
        /// notification toast — its contents end up in the PNG. That is
        /// somebody's private screen, and the whole point of the throwaway
        /// database and the synthetic fixture is that a capture can never
        /// contain one. So the region is sampled on a grid before every
        /// read-back, and a capture is refused unless the entire grid is ours.
        /// </summary>
        public static bool IsOwnedByThisProcess(PixelRect region)
        {
            const int GA_ROOT = 2;
            const int steps = 5;

            uint self = (uint)Environment.ProcessId;

            for (int row = 0; row < steps; row++)
            for (int column = 0; column < steps; column++)
            {
                // Inset by one pixel: the outermost row and column of a
                // window's rectangle can belong to the window behind it.
                int x = region.X + 1 +
                    (int)((region.Width - 2L) * column / (steps - 1.0));
                int y = region.Y + 1 +
                    (int)((region.Height - 2L) * row / (steps - 1.0));

                IntPtr handle = WindowFromPoint(new POINT { X = x, Y = y });

                if (handle == IntPtr.Zero)
                    return false;

                IntPtr root = GetAncestor(handle, GA_ROOT);

                if (root == IntPtr.Zero)
                    root = handle;

                GetWindowThreadProcessId(root, out uint owner);

                if (owner != self)
                    return false;
            }

            return true;
        }

        public static Visual? FindNamed(Visual root, string name) =>
            root.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => control.Name == name);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, int flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window, out uint processId);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr window, int attribute, out RECT value, int size);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(
            IntPtr dc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(
            IntPtr dc, int x, int y, int width, int height,
            IntPtr sourceDc, int sourceX, int sourceY, uint rop);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            IntPtr dc, IntPtr bitmap, uint start, uint lines,
            byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    }

    internal readonly record struct RegionDifference(
        double Mean,
        double ChangedShare,
        long Pixels)
    {
        public static RegionDifference Empty { get; } = new(-1, -1, 0);

        public bool Measured => Pixels > 0;
    }
}
