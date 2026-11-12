using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;

namespace AIUsageDock.Pages;

/// <summary>
/// Draws a usage bar as a small PNG: a thin rounded track with a rounded fill and an
/// optional pace tick, like the tray panel of Win-CodexBar. Adaptive Cards cannot draw
/// a bar thinner than a padded container, so the bar is an image that stretches to the
/// card width; its 100:1 shape keeps it a few pixels tall.
/// </summary>
internal static class BarImage
{
    internal const int Width = 1200;
    internal const int Height = 12;

    // Half-transparent grey reads as a track on both the light and the dark palette.
    private static readonly (byte R, byte G, byte B, byte A) Track = (128, 128, 128, 72);

    private static readonly ConcurrentDictionary<(int, uint, int), string> Cache = new();

    /// <summary>A data URI for a bar filled to <paramref name="percent"/> in <paramref name="rgb"/> (0xRRGGBB).</summary>
    /// <param name="pacePercent">Where an even spend of the window would stand; null draws no tick.</param>
    public static string DataUri(int percent, uint rgb, int? pacePercent = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        var pace = pacePercent is { } value ? Math.Clamp(value, 0, 100) : -1;
        return Cache.GetOrAdd((percent, rgb, pace), key =>
            "data:image/png;base64," + Convert.ToBase64String(Png(Pixels(key.Item1, key.Item2, key.Item3 < 0 ? null : key.Item3))));
    }

    /// <summary>The bar as RGBA rows, before encoding.</summary>
    internal static byte[] Pixels(int percent, uint rgb, int? pacePercent)
    {
        var pixels = new byte[Width * Height * 4];
        var fillEnd = Width * percent / 100d;
        // A sliver of fill would be narrower than its own rounded ends.
        if (percent > 0)
        {
            fillEnd = Math.Max(fillEnd, Height);
        }

        var tick = pacePercent is { } pace ? Width * pace / 100d : double.NaN;
        var (r, g, b) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var track = Coverage(x, y, 0, Width);
                var fill = percent > 0 ? Coverage(x, y, 0, fillEnd) : 0;

                // Fill over track, both straight alpha.
                var trackAlpha = track * Track.A / 255d * (1 - fill);
                var alpha = fill + trackAlpha;
                double red = 0, green = 0, blue = 0;
                if (alpha > 0)
                {
                    red = (r * fill + Track.R * trackAlpha) / alpha;
                    green = (g * fill + Track.G * trackAlpha) / alpha;
                    blue = (b * fill + Track.B * trackAlpha) / alpha;
                }

                // The pace tick: light where it crosses the fill, dark on the empty track.
                if (Math.Abs(x + 0.5 - tick) <= 2 && track > 0)
                {
                    var onFill = fill > 0.5;
                    (red, green, blue) = onFill ? (255d, 255d, 255d) : (96d, 96d, 96d);
                    alpha = track * (onFill ? 0.9 : 0.8);
                }

                var at = (y * Width + x) * 4;
                pixels[at] = (byte)Math.Round(red);
                pixels[at + 1] = (byte)Math.Round(green);
                pixels[at + 2] = (byte)Math.Round(blue);
                pixels[at + 3] = (byte)Math.Round(alpha * 255);
            }
        }

        return pixels;
    }

    /// <summary>How much of a pixel lies inside the pill spanning x0..x1, from 0 to 1.</summary>
    private static double Coverage(int x, int y, double x0, double x1)
    {
        const double radius = Height / 2d;
        var px = x + 0.5;
        var py = y + 0.5;
        if (px < x0 || px > x1)
        {
            return 0;
        }

        var centre = px < x0 + radius ? x0 + radius : px > x1 - radius ? x1 - radius : px;
        var distance = Math.Sqrt((px - centre) * (px - centre) + (py - radius) * (py - radius));
        return Math.Clamp(radius - distance + 0.5, 0, 1);
    }

    private static byte[] Png(byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8;  // bits per channel
        header[9] = 6;  // RGBA
        Chunk(output, "IHDR", header);

        using (var data = new MemoryStream())
        {
            using (var zlib = new ZLibStream(data, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                for (var y = 0; y < Height; y++)
                {
                    zlib.WriteByte(0); // no row filter
                    zlib.Write(rgba, y * Width * 4, Width * 4);
                }
            }

            Chunk(output, "IDAT", data.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);

        Span<byte> name = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        output.Write(name);
        output.Write(data);

        var crc = Crc(Crc(0xFFFFFFFF, name), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc;
    }
}
