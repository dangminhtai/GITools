using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GctFov;

public sealed class FpsConfig
{
    public const string DefaultPath = @"C:\GCT\Backends\z3lx-7.1.0\plugin_config.json";
    private readonly string path, mutexName;
    private bool backedUp;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public FpsConfig(string configPath)
    {
        path = Path.GetFullPath(configPath);
        // Same mutex as the existing GCT writer, so the two apps serialize writes.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        mutexName = "GCT.FovConfig." + key;
    }

    public int Read() => Locked(() => Parse(ReadBytes()).Root["targetFps"]!.GetValue<int>());

    public void Write(int value) => Locked(() =>
    {
        if (value is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(value));
        var before = ReadBytes();
        var document = Parse(before);
        document.Root["targetFps"] = value;
        document.Root["unlockFps"] = true;
        var bytes = Encoding.UTF8.GetBytes(document.Root.ToJsonString(Json) + "\n");
        // z3lx holds this file open without FILE_SHARE_DELETE. Keep the SAME file:
        // replacing/renaming it breaks its handle and Modified-only change watcher.
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (!ReadStream(file).AsSpan().SequenceEqual(before)) throw new IOException("Config changed.");
            if (!backedUp)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GCT-FPS", "backups");
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"), before);
                backedUp = true;
            }
            file.Position = 0;
            file.Write(bytes);
            file.SetLength(bytes.Length);
            file.Flush(true);
        }
        Thread.Sleep(65);
        var after = Parse(ReadBytes()).Root;
        if (!JsonNode.DeepEquals(document.Root, after)) throw new IOException("Config changed after write.");
        return true;
    });

    private byte[] ReadBytes()
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var first = ReadStream(file);
        Thread.Sleep(20);
        var second = ReadStream(file);
        if (!first.AsSpan().SequenceEqual(second)) throw new IOException("Config is being updated.");
        return second;
    }

    private static byte[] ReadStream(FileStream file)
    {
        if (file.Length is <= 0 or > 65536) throw new InvalidDataException("Invalid config size.");
        file.Position = 0;
        var data = new byte[(int)file.Length];
        file.ReadExactly(data);
        return data;
    }

    private static (JsonObject Root, int Value) Parse(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) data = data[3..];
        using var parsed = JsonDocument.Parse(data.ToArray());
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var names = new HashSet<string>();
        foreach (var property in parsed.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate field.");
        var root = JsonNode.Parse(data)!.AsObject();
        var value = root["targetFps"]!.GetValue<int>();
        if (value is < 1 or > 1000 || root["unlockFps"] is null) throw new InvalidDataException();
        _ = root["unlockFps"]!.GetValue<bool>();
        return (root, value);
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(500); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Another writer is busy.");
            return action();
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }
}
