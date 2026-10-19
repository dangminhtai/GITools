using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GctFov;

public sealed record TrialTarget(string Owner, string Target, string Camera, uint Thread, double[] Position);
public sealed record ArgumentTrialState(int Pid, long Session, string Phase, string Reason, string Nonce,
    ulong Overrides, long LastOverride, long LastOriginal, TrialTarget[] Bindings, bool SupportsXyz, int Axis, bool SupportsRotation,
    bool SupportsCustomPosition, double CustomPositionLimit, string ArgumentKind, bool SupportsKeyboard, double[] AppliedPosition);

// Only publishes an explicitly requested, session-bound persistent pose request.
// Does not load DLLs or claim that a sampled target is the rendering camera.
public sealed class ArgumentTrialClient(string configPath)
{
    private readonly string folder = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
    private (int Pid, long Session)? ownSession;

    public ArgumentTrialState Read()
    {
        using var file = new FileStream(Path.Combine(folder, "pose_arg_trial_state.json"), FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length is <= 0 or > 16384) throw new InvalidDataException("Invalid trial state.");
        using var document = JsonDocument.Parse(file);
        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1 ||
            root.GetProperty("mode").GetString() != "argument_x_visual_trial") throw new InvalidDataException();
        var now = Stopwatch.GetTimestamp();
        var frequency = root.GetProperty("qpc_frequency").GetInt64();
        var heartbeat = root.GetProperty("heartbeat_qpc").GetInt64();
        if (frequency != Stopwatch.Frequency || heartbeat > now || now - heartbeat > frequency * 2)
            throw new IOException("Trial backend is not responding.");
        var pid = root.GetProperty("pid").GetInt32();
        var processes = Process.GetProcessesByName("GenshinImpact");
        try { if (!processes.Any(p => p.Id == pid)) throw new IOException("Game session ended."); }
        finally { foreach (var process in processes) process.Dispose(); }
        var bindings = root.GetProperty("bindings").EnumerateArray().Select(b => new TrialTarget(
            Pointer(b.GetProperty("owner").GetString()), Pointer(b.GetProperty("target").GetString()),
            Pointer(b.GetProperty("camera").GetString()), b.GetProperty("thread_id").GetUInt32(), ReadPosition(b))).ToArray();
        var custom = root.TryGetProperty("supports_custom_position", out var customValue) && customValue.GetBoolean();
        var persistent = root.TryGetProperty("supports_persistent_pose", out var persistentValue) && persistentValue.GetBoolean();
        if (!persistent) throw new InvalidOperationException("Cần nạp plugin giữ camera mới.");
        var limit = double.MaxValue;
        return new(pid, root.GetProperty("start_qpc").GetInt64(), root.GetProperty("phase").GetString()!,
            root.GetProperty("reason").GetString()!, root.GetProperty("nonce").GetString()!,
            root.GetProperty("argument_override_count").GetUInt64(), root.GetProperty("last_override_qpc").GetInt64(),
            root.GetProperty("last_original_argument_qpc").GetInt64(), bindings,
            root.TryGetProperty("supports_xyz", out var xyz) && xyz.GetBoolean(),
            root.TryGetProperty("axis", out var axis) ? Math.Clamp(axis.GetInt32(), 0, 4) : 0,
            root.TryGetProperty("supports_rotation", out var rotation) && rotation.GetBoolean(), custom, limit,
            root.TryGetProperty("argument_kind", out var kind) ? kind.GetString()! : "position",
            root.TryGetProperty("supports_keyboard_movement", out var keyboard) && keyboard.GetBoolean(),
            root.TryGetProperty("applied_position", out var applied) ? applied.EnumerateArray().Select(v => v.GetDouble()).ToArray() : []);
    }

    private static double[] ReadPosition(JsonElement binding)
    {
        if (!binding.TryGetProperty("position", out var value)) return [];
        var position = value.EnumerateArray().Select(c => c.GetDouble()).ToArray();
        if (position.Length != 3 || !position.All(double.IsFinite)) throw new InvalidDataException("Invalid position observation.");
        return position;
    }

    private static string Pointer(string? text)
    {
        if (text == null || !text.StartsWith("0x", StringComparison.Ordinal) ||
            !ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value == 0)
            throw new InvalidDataException("Invalid observed pointer.");
        return text;
    }

    public void Start(int axis = 0)
    {
        if (axis is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(axis));
        var state = Read();
        if (axis != 0 && !state.SupportsXyz) throw new InvalidOperationException("Cần nạp plugin bản thử Y/Z.");
        if (axis >= 3 && !state.SupportsRotation) throw new InvalidOperationException("Cần nạp plugin bản thử yaw/pitch.");
        if (state.Bindings.Length != 1)
            throw new InvalidOperationException("Chưa có một đích camera duy nhất để thử.");
        var b = state.Bindings[0];
        var nonce = Guid.NewGuid().ToString("N");
        var command = state.SupportsRotation
            ? FormattableString.Invariant($"GCTARGPOSE2 {state.Pid} {state.Session} {b.Owner} {b.Thread} {b.Target} {b.Camera} {nonce} {axis} {(axis < 3 ? "0.25" : "5")}\n")
            : state.SupportsXyz
            ? FormattableString.Invariant($"GCTARGXYZ1 {state.Pid} {state.Session} {b.Owner} {b.Thread} {b.Target} {b.Camera} {nonce} {axis} 0.25\n")
            : FormattableString.Invariant($"GCTARGX1 {state.Pid} {state.Session} {b.Owner} {b.Thread} {b.Target} {b.Camera} {nonce} 0.25\n");
        Publish(command);
        ownSession = (state.Pid, state.Session);
    }

    public void Stop(bool onlyOwnSession = false)
    {
        if (onlyOwnSession && ownSession == null) return;
        var state = Read();
        if (onlyOwnSession && ownSession != (state.Pid, state.Session)) return;
        Publish(FormattableString.Invariant($"GCTARGXSTOP1 {state.Pid} {state.Session}\n"));
        ownSession = null;
    }

    public (int Pid, long Session, string Nonce) StartKeyboard(double speed)
    {
        if (!double.IsFinite(speed) || speed <= 0 || speed > float.MaxValue)
            throw new ArgumentException("Tốc độ phải là số dương hữu hạn.");
        var state = Read();
        if (!state.SupportsKeyboard || state.Bindings.Length != 1)
            throw new InvalidOperationException("Cần plugin hỗ trợ di chuyển bằng phím và một đích camera.");
        var b = state.Bindings[0];
        var nonce = Guid.NewGuid().ToString("N");
        Publish(FormattableString.Invariant($"GCTARGMOVE2 {state.Pid} {state.Session} {b.Owner} {b.Thread} {b.Target} {b.Camera} {nonce} {speed:R}\n"));
        ownSession = (state.Pid, state.Session);
        return (state.Pid, state.Session, nonce);
    }

    public void StartPosition(double x, double y, double z, bool offset)
    {
        double[] coordinates = [x, y, z];
        if (!coordinates.All(v => double.IsFinite(v) && Math.Abs(v) <= float.MaxValue)) throw new ArgumentException("XYZ phải là số hữu hạn.");
        var state = Read();
        if (!state.SupportsCustomPosition || state.Bindings.Length != 1 || state.Bindings[0].Position.Length != 3) throw new InvalidOperationException("Chưa sẵn sàng chỉnh XYZ.");
        var b = state.Bindings[0];
        var magic = offset ? "GCTARGOFFSET2" : "GCTARGABS2";
        var nonce = Guid.NewGuid().ToString("N");
        Publish(FormattableString.Invariant($"{magic} {state.Pid} {state.Session} {b.Owner} {b.Thread} {b.Target} {b.Camera} {nonce} {x:R} {y:R} {z:R}\n"));
        ownSession = (state.Pid, state.Session);
    }

    private void Publish(string command)
    {
        var path = Path.Combine(folder, "pose_arg_trial_request.txt");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, command, new UTF8Encoding(false));
            for (int attempt = 0; ; ++attempt)
            {
                try { File.Move(temporary, path, true); return; }
                catch (IOException) when (attempt < 2) { Thread.Sleep(40); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
