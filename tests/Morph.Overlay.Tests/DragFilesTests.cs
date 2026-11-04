using System.Text;

using Morph.Overlay.Wheel;

namespace Morph.Overlay.Tests;

public sealed class DragFilesTests
{
    [Fact]
    public void Descriptor_yields_leaf_names_and_skips_folders()
    {
        var bytes = Descriptor(("report.pdf", 0), (@"Inbox\photo.jpg", 0), ("Folder", 0x10));

        Assert.Equal(["report.pdf", "photo.jpg", null], DragFiles.ParseDescriptorEntries(bytes));
    }

    [Fact]
    public void Truncated_descriptor_reads_only_complete_entries()
    {
        var bytes = Descriptor(("a.png", 0), ("b.png", 0));

        Assert.Equal(["a.png"], DragFiles.ParseDescriptorEntries(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Theory]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("../x/../y.txt", "y.txt")]
    [InlineData("a:b?.jpg", "a_b_.jpg")]
    [InlineData("CON.txt", "attachment.txt")]
    [InlineData("com1", "attachment")]
    [InlineData("  ", "attachment")]
    [InlineData("name.", "name")]
    public void Names_cannot_escape_the_folder(string raw, string expected) =>
        Assert.Equal(expected, DragFiles.SafeName(raw));

    // FILEGROUPDESCRIPTORW: count, then 592-byte FILEDESCRIPTORW entries (flags @0, attributes @36, name @72).
    private static byte[] Descriptor(params (string Name, uint Attributes)[] files)
    {
        var bytes = new byte[4 + files.Length * 592];
        BitConverter.TryWriteBytes(bytes, (uint)files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            var entry = bytes.AsSpan(4 + i * 592, 592);
            BitConverter.TryWriteBytes(entry, 0x4u); // FD_ATTRIBUTES
            BitConverter.TryWriteBytes(entry[36..], files[i].Attributes);
            Encoding.Unicode.GetBytes(files[i].Name).CopyTo(entry[72..]);
        }

        return bytes;
    }
}
