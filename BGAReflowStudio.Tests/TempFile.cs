using System;
using System.IO;

namespace BGAReflowStudio.Tests;

/// <summary>A unique temp file path that is deleted on dispose (used for INI round-trip tests).</summary>
internal sealed class TempFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "arstest_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { /* ignore */ }
    }
}
