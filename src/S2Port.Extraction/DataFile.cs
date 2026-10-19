using System.Collections.Concurrent;
using System.IO.Compression;

namespace S2Port.Extraction;

/// <summary>
/// Reads the extracted data, which is either a folder or a single archive (data.bin, a zip). Paths stay ordinary paths:
/// "…/data.bin/zones/zone_11/act_1/level.json" names an entry inside data.bin, and the rest of the code just combines
/// paths with the data "directory" as before. A path under no archive reads the file system. Entries are read once and
/// cached; reads are thread-safe (the trainer runs several games at once).
/// </summary>
public static class DataFile
{
    private sealed class Archive
    {
        public readonly string Path;
        public readonly ZipArchive Zip;
        public readonly Dictionary<string, ZipArchiveEntry> Entries;
        public readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
        public readonly object Lock = new();

        public Archive(string path)
        {
            Path = path;
            Zip = ZipFile.OpenRead(path);
            Entries = Zip.Entries.Where(e => e.Name.Length > 0)
                .ToDictionary(e => e.FullName.Replace('\\', '/'), e => e, StringComparer.Ordinal);
        }
    }

    private static readonly ConcurrentDictionary<string, Archive> Archives = new(StringComparer.Ordinal);

    /// <summary>True when <paramref name="path"/> is an archive file (a zip) rather than a folder.</summary>
    public static bool IsArchive(string path)
    {
        if (!File.Exists(path)) return false;
        using var f = File.OpenRead(path);
        return f.Length >= 4 && f.ReadByte() == 'P' && f.ReadByte() == 'K';
    }

    /// <summary>Forgets an archive (after it was rebuilt), so the next read opens the new one.</summary>
    public static void Forget(string archivePath)
    {
        if (Archives.TryRemove(Path.GetFullPath(archivePath), out var a)) a.Zip.Dispose();
    }

    /// <summary>The archive a path lies in and the entry name inside it, or null for a plain file-system path.</summary>
    private static (Archive Archive, string Entry)? Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (var (root, a) in Archives)
            if (full.Length > root.Length && full.StartsWith(root, StringComparison.Ordinal) && full[root.Length] == Path.DirectorySeparatorChar)
                return (a, full[(root.Length + 1)..].Replace('\\', '/'));
        for (string? dir = Path.GetDirectoryName(full); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir)) return null;   // reached a real folder: no archive on the way
            if (!IsArchive(dir)) continue;
            var archive = Archives.GetOrAdd(dir, d => new Archive(d));
            return (archive, full[(dir.Length + 1)..].Replace('\\', '/'));
        }
        return null;
    }

    public static bool Exists(string path) => Resolve(path) is { } r ? r.Archive.Entries.ContainsKey(r.Entry) : File.Exists(path);

    public static byte[] ReadAllBytes(string path)
    {
        if (Resolve(path) is not { } r) return File.ReadAllBytes(path);
        return r.Archive.Cache.GetOrAdd(r.Entry, entry =>
        {
            if (!r.Archive.Entries.TryGetValue(entry, out var e)) throw new FileNotFoundException($"{entry} is not in {r.Archive.Path}", path);
            lock (r.Archive.Lock)
            {
                using var s = e.Open();
                using var m = new MemoryStream((int)e.Length);
                s.CopyTo(m);
                return m.ToArray();
            }
        });
    }

    public static string ReadAllText(string path) => Resolve(path) is null
        ? File.ReadAllText(path)
        : System.Text.Encoding.UTF8.GetString(ReadAllBytes(path)).TrimStart('﻿');

    public static Stream OpenRead(string path) => Resolve(path) is null ? File.OpenRead(path) : new MemoryStream(ReadAllBytes(path), writable: false);

    public static bool DirectoryExists(string path)
    {
        if (Resolve(Path.Combine(path, "_")) is not { } r) return Directory.Exists(path);
        string prefix = r.Entry[..^1];
        return r.Archive.Entries.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>The immediate sub-folders of a folder (full paths), in an archive or on disk.</summary>
    public static string[] GetDirectories(string path)
    {
        if (Resolve(Path.Combine(path, "_")) is not { } r) return Directory.GetDirectories(path);
        string prefix = r.Entry[..^1];
        return r.Archive.Entries.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) > 0)
            .Select(k => k[prefix.Length..k.IndexOf('/', prefix.Length)])
            .Distinct()
            .Select(d => Path.Combine(path, d))
            .ToArray();
    }

    /// <summary>
    /// Packs a folder into an archive (replacing it), then deletes the folder: how extraction produces data.bin.
    /// </summary>
    public static void Pack(string folder, string archivePath)
    {
        Forget(archivePath);
        string temp = archivePath + ".new";
        if (File.Exists(temp)) File.Delete(temp);
        ZipFile.CreateFromDirectory(folder, temp, CompressionLevel.Optimal, includeBaseDirectory: false);
        File.Move(temp, archivePath, overwrite: true);
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>Unpacks an archive into a working folder (to add to it), returning the folder.</summary>
    public static string Unpack(string archivePath)
    {
        Forget(archivePath);
        string folder = archivePath + ".work";
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        ZipFile.ExtractToDirectory(archivePath, folder);
        return folder;
    }
}
