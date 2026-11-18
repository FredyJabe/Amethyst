using System.Diagnostics;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Amethyst;

/// <summary>
/// One monotonic world clock shared by every player. The 26.3 client uses this
/// world-clock record to animate sunrise, sky darkness, moon and the sun.
/// Chunk skylight is never reduced for nighttime: the client applies day/night.
/// </summary>
public sealed class WorldClock
{
    public const long TicksPerDay = 24_000;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly double _clockTicksPerSecond;
    private readonly bool _advancing;
    private readonly string? _statePath;
    private readonly object _storageLock = new();
    private readonly long _startClockTime;
    private readonly long _startWorldAge;

    public sealed class ClockState
    {
        public int FormatVersion { get; set; } = 1;
        public long DayTimeTicks { get; set; }
        public long WorldAgeTicks { get; set; }
    }

    public WorldClock(ServerOptions options)
    {
        _clockTicksPerSecond = TicksPerDay / (options.DayLengthMinutes * 60d);
        _advancing = options.DaylightCycle;
        _startClockTime = options.StartTimeTicks;
        if (options.GeneratorVersion >= 5 && options.SaveGeneratedChunks)
        {
            _statePath = Path.Combine(Path.GetFullPath(options.WorldPath), "time.yaml");
            if (File.Exists(_statePath))
            {
                try
                {
                    using var reader = File.OpenText(_statePath);
                    var state = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .Build().Deserialize<ClockState>(reader);
                    if (state is null || state.FormatVersion != 1 ||
                        state.DayTimeTicks < 0 || state.WorldAgeTicks < 0)
                        throw new InvalidDataException("Invalid world clock state.");
                    _startClockTime = state.DayTimeTicks;
                    _startWorldAge = state.WorldAgeTicks;
                }
                catch (YamlException ex)
                {
                    throw new InvalidDataException($"Invalid world time file '{_statePath}'.", ex);
                }
            }
        }
    }

    public long WorldAgeTicks => checked(_startWorldAge + (long)(_watch.Elapsed.TotalSeconds * 20d));
    public double CurrentClockTime => _startClockTime +
        (_advancing ? _watch.Elapsed.TotalSeconds * _clockTicksPerSecond : 0d);
    public long DayTime => (long)Math.Floor(CurrentClockTime) % TicksPerDay;

    // Set Time (Play 0x73) in 26.3: World Age Int64, clock entry count VarInt,
    // then (Clock registry ID VarInt, Time VarLong, Fraction Float, Rate Float).
    public void WritePacket(PacketWriter writer)
    {
        double time = CurrentClockTime;
        long whole = (long)Math.Floor(time);
        writer.WriteInt64(WorldAgeTicks);
        writer.WriteVarInt(1);
        writer.WriteVarInt(VanillaRegistries.IndexOf("minecraft:world_clock", "minecraft:overworld"));
        writer.WriteVarLong(whole);
        writer.WriteFloat((float)(time - whole));
        writer.WriteFloat(_advancing ? (float)(_clockTicksPerSecond / 20d) : 0f);
    }

    public Task SendAsync(PacketChannel channel, CancellationToken ct) =>
        channel.SendAsync(0x73, WritePacket, ct);

    public void Save()
    {
        if (_statePath is null) return;
        lock (_storageLock)
        {
            var state = new ClockState
            {
                DayTimeTicks = (long)Math.Floor(CurrentClockTime),
                WorldAgeTicks = WorldAgeTicks
            };
            string yaml = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build().Serialize(state);
            string temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                File.WriteAllText(temp, yaml, Encoding.UTF8);
                File.Move(temp, _statePath, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
