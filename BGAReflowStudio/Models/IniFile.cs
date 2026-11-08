using System.IO;
using System.Text;

namespace BGAReflowStudio.Models;

/// <summary>
/// Minimal managed INI reader/writer used for the settings and profiles files.
/// Reads/writes the whole file, so no Win32 dependency is required.
/// </summary>
public static class IniFile
{
    private static readonly object Gate = new();

    public static string Read(string section, string key, string fileName, string defaultValue = "")
    {
        lock (Gate)
        {
            if (!File.Exists(fileName)) return defaultValue;

            string? current = null;
            foreach (var raw in File.ReadAllLines(fileName, Encoding.Default))
            {
                var line = raw.TrimEnd();
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    current = trimmed[1..^1].Trim();
                    continue;
                }

                if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;

                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;

                var k = trimmed[..eq].Trim();
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    return trimmed[(eq + 1)..].Trim();
            }

            return defaultValue;
        }
    }

    /// <summary>Remove an entire section (its header and all its keys) from the file.</summary>
    public static void DeleteSection(string section, string fileName)
    {
        lock (Gate)
        {
            if (!File.Exists(fileName)) return;
            var outp = new List<string>();
            bool inSection = false;
            foreach (var line in File.ReadAllLines(fileName, Encoding.Default))
            {
                var t = line.Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    inSection = string.Equals(t[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase);
                    if (inSection) continue; // drop the header
                }
                if (!inSection) outp.Add(line);
            }
            File.WriteAllLines(fileName, outp, Encoding.Default);
        }
    }

    public static void Write(string section, string key, string value, string fileName)
    {
        lock (Gate)
        {
            var lines = File.Exists(fileName)
                ? new List<string>(File.ReadAllLines(fileName, Encoding.Default))
                : new List<string>();

            int sectionStart = -1;
            int sectionEnd = lines.Count; // exclusive
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    var name = t[1..^1].Trim();
                    if (sectionStart >= 0) { sectionEnd = i; break; }
                    if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                        sectionStart = i;
                }
            }

            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length != 0) lines.Add("");
                lines.Add($"[{section}]");
                lines.Add($"{key}={value}");
            }
            else
            {
                bool replaced = false;
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var t = lines[i].Trim();
                    var eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    if (string.Equals(t[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        replaced = true;
                        break;
                    }
                }

                if (!replaced)
                {
                    int insert = sectionEnd;
                    while (insert - 1 > sectionStart && lines[insert - 1].Trim().Length == 0) insert--;
                    lines.Insert(insert, $"{key}={value}");
                }
            }

            File.WriteAllLines(fileName, lines, Encoding.Default);
        }
    }
}
