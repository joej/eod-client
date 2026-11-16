// SPDX-License-Identifier: GPL-3.0-or-later
//
// The converter's file reads, so a run can record what it opens (the file-usage audit, `mpk usage`). With DAOC_FILE_LOG
// set, each distinct file read and archive member extracted is written as "path<TAB>member" (member empty for a loose file
// or an archive's directory) to "<DAOC_FILE_LOG>.<process id>": one file per process, since a parallel make's processes
// appending to one file interleaved their lines. The patch files went unnoticed because the audits only looked at what
// the converter already read; this records what it never opens.

using System.Text;

namespace DaocAssets;

/// <summary>File reads that a conversion run can log (DAOC_FILE_LOG); otherwise plain <see cref="File"/> calls.</summary>
public static class InstallFiles
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("DAOC_FILE_LOG");
    private static readonly object Gate = new();
    private static readonly HashSet<string> Seen = [];
    private static StreamWriter? _log;

    /// <summary>Records that <paramref name="path"/> (or its archive member <paramref name="member"/>) was read.</summary>
    public static void Note(string path, string? member = null)
    {
        if (LogPath is null)
            return;
        string line = $"{Path.GetFullPath(path)}\t{member}\n";
        lock (Gate)
        {
            if (!Seen.Add(line))
                return;
            _log ??= new StreamWriter($"{LogPath}.{Environment.ProcessId}", append: true, new UTF8Encoding(false)) { AutoFlush = true };
            _log.Write(line);
        }
    }

    public static byte[] ReadAllBytes(string path) { Note(path); return File.ReadAllBytes(path); }
    public static string ReadAllText(string path) { Note(path); return File.ReadAllText(path); }
    public static string ReadAllText(string path, Encoding encoding) { Note(path); return File.ReadAllText(path, encoding); }
    public static string[] ReadAllLines(string path) { Note(path); return File.ReadAllLines(path); }
    public static string[] ReadAllLines(string path, Encoding encoding) { Note(path); return File.ReadAllLines(path, encoding); }
    public static IEnumerable<string> ReadLines(string path) { Note(path); return File.ReadLines(path); }
    public static void Copy(string from, string to, bool overwrite) { Note(from); File.Copy(from, to, overwrite); }
}
