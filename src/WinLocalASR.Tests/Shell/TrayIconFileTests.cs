using System.Drawing;
using System.Runtime.Versioning;
using Xunit;

namespace WinLocalASR.Tests.Shell;

/// <summary>
/// The four committed tray icons (src/WinLocalASR.App/Assets) are structurally valid ICO
/// containers cross-platform, and load through GDI+ on Windows (CI runs the SkippableFact
/// for real; macOS skips it).
/// </summary>
public class TrayIconFileTests
{
    private static string AssetsDir
    {
        get
        {
            // Tests bin: src/WinLocalASR.Tests/bin/Debug/net10.0 → four levels up = src/.
            string dir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "WinLocalASR.App", "Assets"));
            return dir;
        }
    }

    public static IEnumerable<object[]> IconFiles()
    {
        foreach (string kind in new[] { "idle", "recording", "processing", "error" })
        {
            yield return new object[] { kind };
        }
    }

    [Theory]
    [MemberData(nameof(IconFiles))]
    public void Ico_files_are_valid_16_and_32_bit_containers(string kind)
    {
        string path = Path.Combine(AssetsDir, $"tray-{kind}.ico");
        Assert.True(File.Exists(path), $"missing icon: {path}");
        byte[] data = File.ReadAllBytes(path);
        Assert.InRange(data.Length, 100, 16 * 1024); // a few KB each

        using BinaryReader reader = new(new MemoryStream(data));
        Assert.Equal(0, reader.ReadUInt16());       // reserved
        Assert.Equal(1, reader.ReadUInt16());       // type = icon
        ushort count = reader.ReadUInt16();
        Assert.Equal(2, count);                     // 16 + 32

        HashSet<int> sizes = new();
        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            reader.ReadByte();                      // color count
            reader.ReadByte();                      // reserved
            Assert.Equal(1, reader.ReadUInt16());   // planes
            Assert.Equal(32, reader.ReadUInt16());  // bits per pixel
            uint bytesInRes = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();

            Assert.Equal(width, height);
            sizes.Add(width);
            Assert.InRange(offset, 6 + 16u * count, (uint)data.Length);
            Assert.InRange(offset + bytesInRes, offset, (uint)data.Length);

            using BinaryReader bmp = new(new MemoryStream(data, (int)offset, (int)bytesInRes));
            Assert.Equal(40u, bmp.ReadUInt32());    // BITMAPINFOHEADER size
            Assert.Equal(width, bmp.ReadInt32());   // biWidth
            Assert.Equal(2 * height, bmp.ReadInt32()); // biHeight (XOR + AND rows)
            Assert.Equal(1, bmp.ReadUInt16());      // planes
            Assert.Equal(32, bmp.ReadUInt16());     // bpp
        }

        Assert.Contains(16, sizes);
        Assert.Contains(32, sizes);
    }

    [SkippableTheory]
    [MemberData(nameof(IconFiles))]
    [SupportedOSPlatform("windows")]
    public void Ico_files_load_through_gdi_plus_on_windows(string kind)
    {
        Skip.IfNot(OperatingSystem.IsWindows());

        string path = Path.Combine(AssetsDir, $"tray-{kind}.ico");
        using FileStream stream = File.OpenRead(path);
        using Icon icon = new(stream);
        Assert.InRange(icon.Width, 1, 64);
        Assert.InRange(icon.Height, 1, 64);
    }
}
