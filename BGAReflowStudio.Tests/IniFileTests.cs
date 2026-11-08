using BGAReflowStudio.Models;
using Xunit;

namespace BGAReflowStudio.Tests;

public class IniFileTests
{
    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        using var f = new TempFile();
        IniFile.Write("General", "Machine", "ACHI IR PRO SC", f.Path);
        Assert.Equal("ACHI IR PRO SC", IniFile.Read("General", "Machine", f.Path));
    }

    [Fact]
    public void Read_MissingKey_ReturnsDefault()
    {
        using var f = new TempFile();
        Assert.Equal("9600", IniFile.Read("Serial", "BaudRate", f.Path, "9600"));
    }

    [Fact]
    public void Write_UpdatesExistingKeyInPlace()
    {
        using var f = new TempFile();
        IniFile.Write("Serial", "BaudRate", "19200", f.Path);
        IniFile.Write("Serial", "BaudRate", "9600", f.Path);
        Assert.Equal("9600", IniFile.Read("Serial", "BaudRate", f.Path));
    }

    [Fact]
    public void DeleteSection_RemovesOnlyThatSection()
    {
        using var f = new TempFile();
        IniFile.Write("0", "Name", "Keep", f.Path);
        IniFile.Write("1", "Name", "Drop", f.Path);

        IniFile.DeleteSection("1", f.Path);

        Assert.Equal("Keep", IniFile.Read("0", "Name", f.Path));
        Assert.Equal("", IniFile.Read("1", "Name", f.Path));
    }

    [Fact]
    public void Read_IsCaseInsensitive_ForSectionAndKey()
    {
        using var f = new TempFile();
        IniFile.Write("General", "Machine", "x", f.Path);
        Assert.Equal("x", IniFile.Read("general", "MACHINE", f.Path));
    }
}
