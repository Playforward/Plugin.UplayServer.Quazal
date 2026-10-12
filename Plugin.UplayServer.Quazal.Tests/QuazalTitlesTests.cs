using ServerShared.Titles;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// How the plugin decides which titles it serves and on which ports. The
/// port is the whole of title resolution 
/// </summary>
public sealed class QuazalTitlesTests
{
    /// <summary>A catalog title in Far Cry 3's family unless told otherwise.</summary>
    private static TitleDescriptor Title(string id, params int[] ports) => Title(id, "farcry3", ports);

    private static TitleDescriptor Title(string id, string family, params int[] ports) => new()
    {
        TitleId = id,
        Name = id,
        ProtocolFamily = family,
        Matchers = new TitleMatchers { Ports = [.. ports] },
    };

    private static QuazalSettings Settings(params (string Id, int Secure)[] titles)
    {
        var settings = new QuazalSettings { Titles = new(StringComparer.OrdinalIgnoreCase) };
        foreach ((string id, int secure) in titles)
            settings.Titles[id] = new QuazalTitleSettings { SecureServerPort = secure };
        return settings;
    }

    [Fact]
    public void TheProtocolFamilyIsTheCatalogsAndIsRefusedRatherThanGuessedAt()
    {
        // Recorded in the catalog, matched by slug regardless of case.
        var byPort = QuazalTitles.Resolve(Settings(("splinter-cell-blacklist", 6573)),
            [Title("splinter-cell-blacklist", "Overlord", 6572, 6573)], out var problems);
        Assert.Empty(problems);
        Assert.Equal(QuazalProtocolFamily.Overlord, byPort[6572].ProtocolFamily);

        // Not recorded: refused, naming what would be accepted.
        QuazalTitles.Resolve(Settings(("farcry3", 6571)), [Title("farcry3", "", 6570, 6571)], out problems);
        Assert.Contains(problems, p => p.Contains("no ProtocolFamily") && p.Contains("farcry3, overlord, futuresoldier"));

        // Recorded as something this plugin has no table for: also refused.
        QuazalTitles.Resolve(Settings(("farcry3", 6571)), [Title("farcry3", "storm", 6570, 6571)], out problems);
        Assert.Contains(problems, p => p.Contains("'storm'") && p.Contains("does not implement"));
    }

    [Fact]
    public void EachServedTitleOwnsItsOwnPorts()
    {
        var byPort = QuazalTitles.Resolve(
            Settings(("farcry3", 6571), ("splinter-cell-blacklist", 6573)),
            [Title("farcry3", 6570, 6571), Title("splinter-cell-blacklist", 6572, 6573)],
            out var problems);

        Assert.Empty(problems);
        Assert.Equal("farcry3", byPort[6570].TitleId);
        Assert.Equal("farcry3", byPort[6571].TitleId);
        Assert.Equal("splinter-cell-blacklist", byPort[6572].TitleId);
        Assert.Equal("splinter-cell-blacklist", byPort[6573].TitleId);
        Assert.Equal(6573, byPort[6572].SecureServerPort);
        Assert.False(byPort.ContainsKey(6574));
    }

    [Fact]
    public void ACatalogTitleNotListedHereIsNotServed()
    {
        var byPort = QuazalTitles.Resolve(
            Settings(("farcry3", 6571)),
            [Title("farcry3", 6570, 6571), Title("splinter-cell-blacklist", 6572, 6573)],
            out var problems);

        Assert.Empty(problems);
        Assert.False(byPort.ContainsKey(6572));
    }

    [Fact]
    public void ATitleMissingFromTheCatalogIsAProblem()
    {
        QuazalTitles.Resolve(Settings(("splinter-cell-blacklist", 6573)), [Title("farcry3", 6570, 6571)], out var problems);
        Assert.Contains(problems, p => p.Contains("splinter-cell-blacklist") && p.Contains(TitleRegistry.ConfigFileName));
    }

    [Fact]
    public void TheSecurePortMustBeOneTheTitleDeclares()
    {
        QuazalTitles.Resolve(Settings(("farcry3", 6573)), [Title("farcry3", 6570, 6571)], out var problems);
        Assert.Contains(problems, p => p.Contains("SecureServerPort 6573"));
    }

    [Fact]
    public void ATitleNeedsTwoPortsToSplitLoginFromSecure()
    {
        QuazalTitles.Resolve(Settings(("farcry3", 6570)), [Title("farcry3", 6570)], out var problems);
        Assert.Contains(problems, p => p.Contains("distinct secure port"));
    }

    [Fact]
    public void ServingNothingIsAProblemRatherThanASilentIdle()
    {
        QuazalTitles.Resolve(Settings(), [Title("farcry3", 6570, 6571)], out var problems);
        Assert.Contains(problems, p => p.Contains("Titles is empty"));
    }

    [Fact]
    public void AConfigFromBeforeTitlesMigratesOntoTheTitleOwningItsPorts()
    {
        var settings = new QuazalSettings
        {
            Titles = new(StringComparer.OrdinalIgnoreCase),
            Ports = [6570, 6571],
            SecureServerPort = 6571,
            RendezVousIdentityFile = "Configs/RendezVousIdentity.json",
        };

        bool changed = QuazalTitles.MigrateLegacyListener(settings, [Title("farcry3", 6570, 6571)], out var notes);

        Assert.True(changed);
        Assert.Equal(6571, settings.Titles["farcry3"].SecureServerPort);
        Assert.Null(settings.Ports);
        Assert.Null(settings.SecureServerPort);
        Assert.Null(settings.RendezVousIdentityFile);
        Assert.Contains(notes, n => n.Contains("migrated legacy Ports"));
        Assert.Contains(notes, n => n.Contains("RendezVousIdentityFile"));
    }

    [Fact]
    public void LegacyPortsNobodyOwnsAreNamedRatherThanGuessedAt()
    {
        var settings = new QuazalSettings { Titles = new(StringComparer.OrdinalIgnoreCase), Ports = [7000, 7001], SecureServerPort = 7001 };

        QuazalTitles.MigrateLegacyListener(settings, [Title("farcry3", 6570, 6571)], out var notes);

        Assert.Empty(settings.Titles);
        Assert.Contains(notes, n => n.Contains("owns legacy port 7000"));
    }

    [Fact]
    public void LegacyFieldsBesideTitlesAreClearedNotMerged()
    {
        var settings = Settings(("farcry3", 6571));
        settings.Ports = [6572, 6573];
        settings.SecureServerPort = 6573;

        bool changed = QuazalTitles.MigrateLegacyListener(settings, [Title("farcry3", 6570, 6571), Title("splinter-cell-blacklist", 6572, 6573)], out var notes);

        Assert.True(changed);
        Assert.Single(settings.Titles);
        Assert.Contains(notes, n => n.Contains("superseded"));
    }

    [Fact]
    public void ACurrentConfigIsLeftAlone()
    {
        var settings = Settings(("farcry3", 6571));
        Assert.False(QuazalTitles.MigrateLegacyListener(settings, [Title("farcry3", 6570, 6571)], out var notes));
        Assert.Empty(notes);
    }
}
