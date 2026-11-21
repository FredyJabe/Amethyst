using Amethyst;
using System.Reflection;
using System.Runtime.Loader;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Amethyst.Shards;

public sealed class ShardManifest
{
    public string Id { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    public int ApiVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public List<string> DependsOn { get; set; } = [];
    public List<string> Provides { get; set; } = [];
    public List<string> Requires { get; set; } = [];
}

public enum ShardState { Loaded, Enabled, Disabled }
public readonly record struct ShardInfo(string Id, ShardState State, string[] Dependencies);

/// <summary>Discovers installed Shards, resolves dependencies, and manages
/// lifecycle transitions. No plug-in assemblies are referenced by the host project.</summary>
public sealed class ShardHost : IAsyncDisposable, IShardPacketBus
{
    // Each Shard resolves its own third-party dependencies while sharing the
    // Amethyst assembly that defines the API/packet types. Shards are deliberately
    // noncollectible: lifecycle disable does not attempt unsafe hot-unloading.
    private sealed class ShardLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public ShardLoadContext(string assemblyPath) : base(isCollectible: false) =>
            _resolver = new AssemblyDependencyResolver(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IShard).Assembly.GetName().Name)
                return typeof(IShard).Assembly;
            string? dependency = _resolver.ResolveAssemblyToPath(name);
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }
    }

    private sealed class Entry
    {
        public required ShardManifest Manifest { get; init; }
        public required IShard Instance { get; init; }
        public required ShardContext Context { get; init; }
        public ShardState State { get; set; } = ShardState.Loaded;
    }

    private sealed class ShardContext : IShardContext
    {
        private readonly ShardHost _host;
        public string Id { get; }
        public ServerOptions Options => _host._options;
        public string DataDirectory { get; }
        public ShardContext(ShardHost host, string id, string dataDirectory)
        {
            _host = host; Id = id; DataDirectory = dataDirectory;
        }
        public void RegisterService<T>(T service) where T : class => _host.Register(Id, service);
        public T GetRequiredService<T>() where T : class => GetService<T>() ??
            throw new InvalidOperationException($"Shard '{Id}' requires service {typeof(T).FullName}.");
        public T? GetService<T>() where T : class => _host.GetService<T>();
        public void Log(string message) => Console.WriteLine($"[shard:{Id}] {message}");
        public void RegisterPlayPacketHandler(int packetId, ShardPlayPacketHandler handler) =>
            _host.RegisterPacket(Id, packetId, handler);
        public void RegisterConsoleCommand(string name, ShardConsoleCommand handler) =>
            _host.RegisterConsole(Id, name, handler);
        public void RegisterSkillTrigger(string trigger) => _host._skills.RegisterTrigger(Id, trigger);
        public void RegisterSkillMechanic(string name, ShardSkillValidator validator,
            ShardSkillExecutor executor) => _host._skills.RegisterMechanic(Id, name, validator, executor);
        public void RegisterSkillDefinitions(string builtInFile, string? overridesDirectory = null) =>
            _host._skills.RegisterDefinitions(Id, builtInFile, overridesDirectory);
    }

    private readonly SkillEngine _skills = new();
    private readonly ServerOptions _options;
    private readonly string _directory;
    private readonly string _dataDirectory;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, (string Owner, object Value)> _services = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _routerLock = new();
    private readonly List<(string Owner, int Id, ShardPlayPacketHandler Handler)> _packets = [];
    private readonly Dictionary<string, (string Owner, ShardConsoleCommand Handler)> _commands =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ShardHost(ServerOptions options, string installedDirectory, string dataDirectory)
    {
        _options = options;
        _directory = Path.GetFullPath(installedDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Register<IShardPacketBus>("amethyst", this);
        Register<IShardSkillEngine>("amethyst", _skills);
    }

    public T? GetService<T>() where T : class
    {
        lock (_services)
            return _services.TryGetValue(typeof(T), out var entry) ? (T)entry.Value : null;
    }

    private void Register<T>(string owner, T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_services)
        {
            if (_services.TryGetValue(typeof(T), out var current) && current.Owner != owner)
                throw new InvalidOperationException($"Service {typeof(T).Name} already supplied by '{current.Owner}'.");
            _services[typeof(T)] = (owner, service);
        }
    }

    public async Task<bool> DispatchAsync(IShardPlayer player, Packet packet, CancellationToken ct)
    {
        ShardPlayPacketHandler[] handlers;
        lock (_routerLock)
            handlers = _packets.Where(p => p.Id == packet.Id &&
                _entries.TryGetValue(p.Owner, out Entry? e) && e.State == ShardState.Enabled)
                .Select(p => p.Handler).ToArray();
        foreach (ShardPlayPacketHandler handler in handlers)
            if (await handler(player, packet, ct)) return true;
        return false;
    }

    private void RegisterPacket(string owner, int packetId, ShardPlayPacketHandler handler)
    {
        if (packetId < 0 || packetId > 0xFF) throw new ArgumentOutOfRangeException(nameof(packetId));
        ArgumentNullException.ThrowIfNull(handler);
        lock (_routerLock) _packets.Add((owner, packetId, handler));
    }

    private void RegisterConsole(string owner, string command, ShardConsoleCommand handler)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(command, "^[a-z][a-z0-9_-]{0,31}$"))
            throw new ArgumentException("Invalid Shard command name.", nameof(command));
        ArgumentNullException.ThrowIfNull(handler);
        lock (_routerLock)
        {
            if (_commands.ContainsKey(command))
                throw new InvalidOperationException($"Console command '{command}' already registered.");
            _commands.Add(command, (owner, handler));
        }
    }

    public string[] RegisteredConsoleCommands
    {
        get { lock (_routerLock) return _commands.Keys.ToArray(); }
    }

    public async Task<bool> TryExecuteConsoleAsync(string line, CancellationToken ct)
    {
        string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        ShardConsoleCommand? command;
        lock (_routerLock)
            command = _commands.TryGetValue(parts[0], out var c) &&
                _entries.TryGetValue(c.Owner, out Entry? e) && e.State == ShardState.Enabled
                ? c.Handler : null;
        if (command is null) return false;
        await command(parts.Length == 2 ? parts[1] : "", ct);
        return true;
    }

    private void Unregister(string owner)
    {
        _skills.Unregister(owner);
        lock (_routerLock)
        {
            _packets.RemoveAll(p => p.Owner == owner);
            foreach (string key in _commands.Where(x => x.Value.Owner == owner).Select(x => x.Key).ToArray())
                _commands.Remove(key);
        }
        lock (_services)
        {
            foreach (var key in _services.Where(x => x.Value.Owner == owner).Select(x => x.Key).ToArray())
                _services.Remove(key);
        }
    }

    public ShardInfo[] Snapshot() => _entries.Values.OrderBy(e => e.Manifest.Id,
        StringComparer.OrdinalIgnoreCase).Select(e => new ShardInfo(e.Manifest.Id, e.State,
        e.Manifest.DependsOn.ToArray())).ToArray();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (_entries.Count != 0) throw new InvalidOperationException("Shards already loaded.");
            if (!Directory.Exists(_directory))
                throw new DirectoryNotFoundException($"Installed Shard directory not found: {_directory}. Build Amethyst.slnx first.");
            var yaml = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            foreach (string file in Directory.EnumerateFiles(_directory, "shard.yaml", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                ShardManifest m;
                try { m = yaml.Deserialize<ShardManifest>(File.ReadAllText(file)) ??
                          throw new InvalidDataException($"Empty Shard manifest: {file}"); }
                catch (YamlException ex) { throw new InvalidDataException($"Malformed Shard manifest: {file}", ex); }
                if (!System.Text.RegularExpressions.Regex.IsMatch(m.Id, "^[a-z][a-z0-9-]{1,48}$") ||
                    m.ApiVersion != 1 || m.DependsOn is null ||
                    m.Provides is null || m.Requires is null ||
                    m.Provides.Concat(m.Requires).Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[a-z][a-z0-9-]{1,48}$")) ||
                    m.DependsOn.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[a-z][a-z0-9-]{1,48}$")))
                    throw new InvalidDataException($"Invalid Shard id, dependencies or API version: {file}");
                if (string.IsNullOrWhiteSpace(m.Assembly) ||
                    Path.GetFileName(m.Assembly) != m.Assembly || !m.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(m.EntryType))
                    throw new InvalidDataException($"Invalid assembly or entry type for Shard '{m.Id}'.");
                if (_entries.ContainsKey(m.Id)) throw new InvalidDataException($"Duplicate Shard '{m.Id}'.");
                string pluginPath = Path.Combine(Path.GetDirectoryName(file)!, m.Assembly);
                if (!File.Exists(pluginPath)) throw new FileNotFoundException($"Missing assembly for Shard '{m.Id}'.", pluginPath);
                // Isolate private plugin dependencies while preserving a single
                // copy of Amethyst's API and authenticated session types.
                var contextLoader = new ShardLoadContext(Path.GetFullPath(pluginPath));
                Assembly assembly = contextLoader.LoadFromAssemblyPath(Path.GetFullPath(pluginPath));
                Type type = assembly.GetType(m.EntryType, throwOnError: true)!;
                if (!typeof(IShard).IsAssignableFrom(type))
                    throw new InvalidDataException($"Entry '{m.EntryType}' does not implement IShard.");
                if (Activator.CreateInstance(type) is not IShard shard)
                    throw new InvalidDataException($"Cannot instantiate '{m.EntryType}'.");
                var context = new ShardContext(this, m.Id, Path.Combine(_dataDirectory, m.Id));
                _entries.Add(m.Id, new Entry { Manifest = m, Instance = shard, Context = context });
            }
            foreach (var entry in _entries.Values)
                foreach (string dep in entry.Manifest.DependsOn)
                    if (!_entries.ContainsKey(dep)) throw new InvalidDataException(
                        $"Shard '{entry.Manifest.Id}' depends on missing Shard '{dep}'.");
            // Preflight dependency cycles before any OnLoad or OnEnable hooks run.
            foreach (Entry active in _entries.Values.Where(e => e.Manifest.Enabled))
                CheckCycles(active.Manifest.Id, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (Entry entry in _entries.Values)
            {
                Directory.CreateDirectory(entry.Context.DataDirectory);
                await entry.Instance.OnLoadAsync(entry.Context, ct);
                Console.WriteLine($"[shards] Loaded {entry.Manifest.Id}");
            }
            foreach (Entry entry in _entries.Values.Where(e => e.Manifest.Enabled))
                await EnableCoreAsync(entry.Manifest.Id, ct);
        }
        finally { _lifecycle.Release(); }
    }

    private void CheckCycles(string id, HashSet<string> active, HashSet<string> visited)
    {
        if (active.Contains(id)) throw new InvalidDataException($"Circular Shard dependency involving '{id}'.");
        if (!visited.Add(id)) return;
        active.Add(id);
        foreach (string dep in _entries[id].Manifest.DependsOn) CheckCycles(dep, active, visited);
        foreach (string capability in _entries[id].Manifest.Requires)
            CheckCycles(FindProvider(capability).Manifest.Id, active, visited);
        active.Remove(id);
    }

    private Entry FindProvider(string capability)
    {
        Entry[] available = _entries.Values.Where(e =>
            e.Manifest.Provides.Contains(capability, StringComparer.OrdinalIgnoreCase) &&
            (e.Manifest.Enabled || e.State == ShardState.Enabled)).ToArray();
        if (available.Length != 1)
            throw new InvalidOperationException($"Capability '{capability}' needs exactly one enabled provider; found {available.Length}. " +
                "Adjust shard.yaml enabled flags.");
        return available[0];
    }

    private async Task EnableCoreAsync(string id, CancellationToken ct,
        HashSet<string>? visiting = null)
    {
        if (!_entries.TryGetValue(id, out Entry? entry))
            throw new KeyNotFoundException($"Shard '{id}' is not installed.");
        if (entry.State == ShardState.Enabled) return;
        visiting ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visiting.Add(id))
            throw new InvalidOperationException($"Circular Shard dependency involving '{id}'.");
        try
        {
            foreach (string dep in entry.Manifest.DependsOn)
                await EnableCoreAsync(dep, ct, visiting);
            foreach (string capability in entry.Manifest.Requires)
                await EnableCoreAsync(FindProvider(capability).Manifest.Id, ct, visiting);
            try
            {
                await entry.Instance.OnEnableAsync(ct);
                entry.State = ShardState.Enabled;
                Console.WriteLine($"[shards] Enabled {id}");
            }
            catch
            {
                Unregister(id);
                throw;
            }
        }
        finally { visiting.Remove(id); }
    }

    public async Task EnableAsync(string id, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try { await EnableCoreAsync(id, ct); }
        finally { _lifecycle.Release(); }
    }

    public async Task DisableAsync(string id, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (!_entries.TryGetValue(id, out Entry? entry)) throw new KeyNotFoundException($"Shard '{id}' is not installed.");
            if (entry.State != ShardState.Enabled) return;
            string[] dependents = _entries.Values.Where(e => e.State == ShardState.Enabled &&
                (e.Manifest.DependsOn.Contains(id, StringComparer.OrdinalIgnoreCase) ||
                 e.Manifest.Requires.Any(cap => entry.Manifest.Provides.Contains(cap, StringComparer.OrdinalIgnoreCase))))
                .Select(e => e.Manifest.Id).ToArray();
            if (dependents.Length != 0) throw new InvalidOperationException(
                $"Cannot disable '{id}' while enabled Shards depend on it: {string.Join(", ", dependents)}.");
            await entry.Instance.OnDisableAsync(ct); // may reject if players are connected
            Unregister(id);
            entry.State = ShardState.Disabled;
            Console.WriteLine($"[shards] Disabled {id}");
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifecycle.WaitAsync();
        try
        {
            // Topological shutdown: dependents stop before their providers.
            var order = new List<Entry>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string id)
            {
                if (!visited.Add(id)) return;
                foreach (string dep in _entries[id].Manifest.DependsOn) Visit(dep);
                foreach (string cap in _entries[id].Manifest.Requires)
                    Visit(FindProvider(cap).Manifest.Id);
                order.Add(_entries[id]);
            }
            foreach (Entry enabled in _entries.Values.Where(e => e.State == ShardState.Enabled))
                Visit(enabled.Manifest.Id);
            foreach (Entry entry in order.AsEnumerable().Reverse())
            {
                if (entry.State != ShardState.Enabled) continue;
                try { await entry.Instance.OnDisableAsync(CancellationToken.None); }
                catch (Exception ex) { Console.Error.WriteLine($"[shards] Error disabling {entry.Manifest.Id}: {ex}"); }
                Unregister(entry.Manifest.Id);
                entry.State = ShardState.Disabled;
            }
        }
        finally { _lifecycle.Release(); _lifecycle.Dispose(); }
    }
}
