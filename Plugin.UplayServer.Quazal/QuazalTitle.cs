using ServerShared.Titles;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// A title as this plugin serves it: what the host's catalog says the game
/// is, plus what the RendezVous transport needs to know about it. Resolved
/// once at start and stored on every <c>Peer</c> at SYN, so nothing
/// downstream interprets a protocol byte without knowing which game sent it.
/// </summary>
internal sealed class QuazalTitle
{
    public required TitleDescriptor Descriptor { get; init; }
    public required QuazalTitleSettings Settings { get; init; }

    public string TitleId => Descriptor.TitleId;
    public string Name => Descriptor.Name;

    /// <summary>The ports this title is reached on; the resolution key.</summary>
    public IReadOnlyList<int> Ports => Descriptor.Matchers.Ports;

    /// <summary>
    /// The port a successful login hands the client on to. Quazal splits
    /// authentication and secure RendezVous across two endpoints, so this
    /// differs from the port the client authenticated on 
    /// </summary>
    public int SecureServerPort => Settings.SecureServerPort;

    public override string ToString() => Descriptor.ToString();

    /// <summary>
    /// The service vocabulary this title is answered from, parsed from the
    /// catalog's slug. <see cref="QuazalProtocolFamily.Unspecified"/> for a
    /// slug this plugin does not implement, which <see cref="QuazalTitles.Resolve"/>
    /// refuses at start.
    /// </summary>
    public QuazalProtocolFamily ProtocolFamily => ParseFamily(Descriptor.ProtocolFamily);

    /// <summary>The catalog slug for each family this plugin implements.</summary>
    public static QuazalProtocolFamily ParseFamily(string? slug) =>
        Enum.TryParse(slug?.Trim(), ignoreCase: true, out QuazalProtocolFamily family) && family != QuazalProtocolFamily.Unspecified
            ? family
            : QuazalProtocolFamily.Unspecified;

    /// <summary>
    /// A title for a test, owning one authentication port and one secure
    /// port and reading no identity file. Far Cry 3's services unless told
    /// otherwise, since that is what the existing wire fixtures speak.
    /// </summary>
    internal static QuazalTitle ForTests(string titleId, int port, int securePort,
        QuazalProtocolFamily family = QuazalProtocolFamily.FarCry3) => new()
    {
        Descriptor = new TitleDescriptor
        {
            TitleId = titleId,
            Name = titleId,
            RendezVousIdentityFile = "",
            ProtocolFamily = family.ToString().ToLowerInvariant(),
            Matchers = new TitleMatchers { Ports = [port, securePort] },
        },
        Settings = new QuazalTitleSettings { SecureServerPort = securePort },
    };

    /// <summary>The map the responder wants, with this title on each of its own ports.</summary>
    internal IReadOnlyDictionary<int, QuazalTitle> OnItsPorts() => Ports.ToDictionary(port => port, _ => this);

    /// <summary>
    /// A map that answers every port with this title, for a harness whose
    /// listening port is whatever the OS hands it. Never for the plugin: a
    /// title that owns every port is the global dispatch this exists to end.
    /// </summary>
    internal IReadOnlyDictionary<int, QuazalTitle> OnEveryPort() => new EveryPort(this);

    private sealed class EveryPort(QuazalTitle title) : IReadOnlyDictionary<int, QuazalTitle>
    {
        public QuazalTitle this[int key] => title;
        public IEnumerable<int> Keys => title.Ports;
        public IEnumerable<QuazalTitle> Values => [title];
        public int Count => 1;
        public bool ContainsKey(int key) => true;
        public bool TryGetValue(int key, out QuazalTitle value)
        {
            value = title;
            return true;
        }
        public IEnumerator<KeyValuePair<int, QuazalTitle>> GetEnumerator() =>
            title.Ports.Select(port => new KeyValuePair<int, QuazalTitle>(port, title)).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// Builds the titles this plugin serves from its config and the host's
/// catalog. Pure: given the same inputs it returns the same map and the same
/// problems, which is what lets the plugin's start-up validation be tested
/// without a running host.
/// </summary>
internal static class QuazalTitles
{
    /// <summary>
    /// Every Quazal-served title keyed by each port it owns. The problems are
    /// exhaustive rather than first-found, so one start reports every
    /// misconfigured title instead of one per restart.
    /// </summary>
    public static IReadOnlyDictionary<int, QuazalTitle> Resolve(
        QuazalSettings settings, IReadOnlyList<TitleDescriptor> catalog, out IReadOnlyList<string> problems)
    {
        Dictionary<int, QuazalTitle> byPort = [];
        List<string> found = [];

        foreach ((string titleId, QuazalTitleSettings? titleSettings) in settings.Titles ?? [])
        {
            TitleDescriptor? descriptor = catalog.FirstOrDefault(t =>
                string.Equals(t.TitleId, titleId, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null)
            {
                found.Add($"Titles['{titleId}'] names a title that is not in Configs/{TitleRegistry.ConfigFileName}");
                continue;
            }
            if (titleSettings is null)
            {
                found.Add($"Titles['{titleId}'] is null");
                continue;
            }

            IReadOnlyList<int> ports = descriptor.Matchers.Ports;
            if (ports.Count < 2)
            {
                found.Add($"'{titleId}' declares {ports.Count} port(s) in Configs/{TitleRegistry.ConfigFileName}; a Quazal title needs an authentication port and a distinct secure port");
                continue;
            }
            if (!ports.Contains(titleSettings.SecureServerPort))
            {
                found.Add($"Titles['{titleId}'].SecureServerPort {titleSettings.SecureServerPort} is not one of the ports '{titleId}' declares ({string.Join("/", ports)})");
                continue;
            }
            if (QuazalTitle.ParseFamily(descriptor.ProtocolFamily) == QuazalProtocolFamily.Unspecified)
            {
                string known = string.Join(", ", Enum.GetNames<QuazalProtocolFamily>().Skip(1).Select(n => n.ToLowerInvariant()));
                found.Add(string.IsNullOrWhiteSpace(descriptor.ProtocolFamily)
                    ? $"'{titleId}' has no ProtocolFamily in the catalog; record one of {known} in the admin panel (or Configs/{TitleRegistry.ConfigFileName})"
                    : $"'{titleId}' has ProtocolFamily '{descriptor.ProtocolFamily}', which this plugin does not implement; it knows {known}");
                continue;
            }

            var title = new QuazalTitle { Descriptor = descriptor, Settings = titleSettings };
            foreach (int port in ports)
            {
                if (!byPort.TryAdd(port, title))
                    found.Add($"port {port} is claimed twice, by '{byPort[port].TitleId}' and '{titleId}'");
            }
        }

        if (byPort.Count == 0 && found.Count == 0)
            found.Add("Titles is empty: no title is served over RendezVous");

        problems = found;
        return byPort;
    }

    /// <summary>
    /// Carries a config written before <see cref="QuazalSettings.Titles"/>
    /// existed forward: the flat port list and scalar secure port become an
    /// entry for whichever catalog title owns those ports. The legacy fields
    /// are cleared so the caller can save the migrated shape and this runs
    /// once rather than on every start.
    /// </summary>
    /// <returns>Whether anything changed and the config should be saved.</returns>
    public static bool MigrateLegacyListener(
        QuazalSettings settings, IReadOnlyList<TitleDescriptor> catalog, out IReadOnlyList<string> notes)
    {
        List<string> found = [];
        notes = found;

        bool hasLegacyPorts = settings.Ports is { Length: > 0 };
        bool hasLegacy = hasLegacyPorts || settings.SecureServerPort is not null
                         || !string.IsNullOrWhiteSpace(settings.RendezVousIdentityFile);
        if (!hasLegacy)
            return false;

        settings.Titles ??= new(StringComparer.OrdinalIgnoreCase);
        if (settings.Titles.Count > 0)
        {
            found.Add("Ports/SecureServerPort are superseded by Titles and were ignored; cleared from the config");
        }
        else if (hasLegacyPorts)
        {
            int[] legacyPorts = settings.Ports!;
            TitleDescriptor? owner = catalog.FirstOrDefault(t => t.Matchers.Ports.Contains(legacyPorts[0]));
            if (owner is null)
            {
                found.Add($"no title in Configs/{TitleRegistry.ConfigFileName} owns legacy port {legacyPorts[0]}; declare its ports there and list it under Titles");
            }
            else if (settings.SecureServerPort is not { } secure)
            {
                found.Add($"legacy Ports name '{owner.TitleId}' but no SecureServerPort was set to carry over; add Titles['{owner.TitleId}'].SecureServerPort");
            }
            else
            {
                settings.Titles[owner.TitleId] = new QuazalTitleSettings { SecureServerPort = secure };
                found.Add($"migrated legacy Ports {string.Join("/", legacyPorts)} and SecureServerPort {secure} into Titles['{owner.TitleId}']; its protocol family is the catalog's");
                foreach (int port in legacyPorts.Where(p => !owner.Matchers.Ports.Contains(p)))
                    found.Add($"legacy port {port} is not one '{owner.TitleId}' declares in the catalog and is no longer listened on");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.RendezVousIdentityFile))
            found.Add($"RendezVousIdentityFile is now per title in Configs/{TitleRegistry.ConfigFileName}; the value here ({settings.RendezVousIdentityFile}) was ignored and cleared");

        settings.Ports = null;
        settings.SecureServerPort = null;
        settings.RendezVousIdentityFile = null;
        return true;
    }
}
