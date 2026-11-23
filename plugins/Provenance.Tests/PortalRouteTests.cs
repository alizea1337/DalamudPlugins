using System.Collections.Generic;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

public class PortalRouteTests
{
    private const uint UltimaThule = 960, Elysion = 1073, Mare = 959, Urqopacha = 1187, Zirgorteh = 1297, Tuliyollal = 1185;

    // Positions as tools/QuestPortals reads them from the game's layout files.
    private static readonly List<Portal> Portals = new()
    {
        new(UltimaThule, 2013072, false, new Vector3(456.7f, 438f, 310.3f), 131483, UltimaThule, new Vector3(330f, 482f, 152.8f)),
        new(UltimaThule, 2013073, false, new Vector3(334.4f, 483.2f, 158.3f), 131484, UltimaThule, new Vector3(461.8f, 437f, 317.5f)),
        new(UltimaThule, 2013074, false, new Vector3(304.3f, 483.5f, 143.1f), 131480, Elysion, new Vector3(-0.1f, 499.9f, 42.3f)),
        new(Elysion, 1043421, true, new Vector3(2.4f, 499.9f, 46.1f), 131482, UltimaThule, new Vector3(308.4f, 482f, 149f)),
        new(Mare, 2013248, false, new Vector3(-172.8f, -47.6f, -298f), 131510, Mare, new Vector3(-26.9f, -132.9f, -454.8f)),
        new(Mare, 2013249, false, new Vector3(-27f, -130.4f, -443.6f), 131509, Mare, new Vector3(-177.4f, -49.2f, -294.3f)),
        new(Urqopacha, 1054640, true, new Vector3(496.1f, 142.2f, 801.6f), 131605, Zirgorteh, new Vector3(-1f, 21.9f, -84f)),
        new(Zirgorteh, 2014858, false, new Vector3(-0.9f, 23.4f, -89.2f), 131606, Urqopacha, new Vector3(499.3f, 142.2f, 791.7f)),
    };

    private static bool Teleports(uint zone) => zone is UltimaThule or Mare or Urqopacha or Tuliyollal;

    private static readonly Vector3 Stigma4 = new(314.9f, 482f, 148.1f);
    private static readonly Vector3 BaseOmicron = new(460f, 437f, 300f);
    private static readonly Vector3 Managingway = new(-203.5f, -48.9f, -273.6f);
    private static readonly Vector3 BestwaysBurrow = new(-20f, -130f, -450f);

    [Fact]
    public void FromBaseOmicronStigma4IsThroughTheA4Portal()
    {
        var leg = PortalRoute.First(UltimaThule, BaseOmicron, UltimaThule, Stigma4, Portals, Teleports);
        Assert.NotNull(leg);
        Assert.Equal(2013072u, leg!.Portal.Holder);
        Assert.False(leg.Elsewhere);
    }

    [Fact]
    public void FromA4OutIntoTheZoneIsThroughTheReturnPortal()
    {
        var leg = PortalRoute.First(UltimaThule, Stigma4, UltimaThule, new Vector3(511, 437, 332), Portals, Teleports);
        Assert.Equal(2013073u, leg!.Portal.Holder);
    }

    [Fact]
    public void FarAcrossTheZoneIsWalked()
    {
        Assert.Null(PortalRoute.First(UltimaThule, BaseOmicron, UltimaThule, new Vector3(-585, 74, 238), Portals, Teleports));
        Assert.Null(PortalRoute.First(UltimaThule, Stigma4, UltimaThule, new Vector3(304.3f, 483.5f, 143.1f), Portals, Teleports));
    }

    [Fact]
    public void ManagingwayIsThroughHopersHold()
    {
        Assert.Equal(2013249u, PortalRoute.First(Mare, BestwaysBurrow, Mare, Managingway, Portals, Teleports)!.Portal.Holder);
        Assert.Equal(2013248u, PortalRoute.First(Mare, Managingway, Mare, new Vector3(-30, -130, -500), Portals, Teleports)!.Portal.Holder);
    }

    [Fact]
    public void ASubZoneIsEnteredFromItsZone()
    {
        var leg = PortalRoute.First(Urqopacha, new Vector3(493, 142, 783), Zirgorteh, new Vector3(75, 2, 50), Portals, Teleports);
        Assert.Equal(1054640u, leg!.Portal.Holder);
        Assert.False(leg.Elsewhere);
    }

    [Fact]
    public void ASubZoneIsEnteredFromElsewhereByGoingToItsZoneFirst()
    {
        var leg = PortalRoute.First(Tuliyollal, Vector3.Zero, Zirgorteh, new Vector3(75, 2, 50), Portals, Teleports);
        Assert.Equal(1054640u, leg!.Portal.Holder);
        Assert.True(leg.Elsewhere);
    }

    [Fact]
    public void LeavingASubZoneForItsZoneUsesTheExit()
        => Assert.Equal(2014858u, PortalRoute.First(Zirgorteh, new Vector3(75, 2, 50), Urqopacha, new Vector3(493, 142, 783), Portals, Teleports) is { } leg ? leg.Portal.Holder : 0);

    [Fact]
    public void ZonesATeleportReachesNeedNoPortal()
        => Assert.Null(PortalRoute.First(Elysion, Vector3.Zero, Mare, Managingway, Portals, Teleports));

    [Fact]
    public void ElysionIsEnteredFromInsideA4()
    {
        // The Elysion portal stands in A-4 Research: first to it (the A-4 portal is worked out for that walk).
        var leg = PortalRoute.First(UltimaThule, BaseOmicron, Elysion, new Vector3(-7, 499, -5), Portals, Teleports);
        Assert.Equal(2013074u, leg!.Portal.Holder);
        Assert.Equal(2013072u, PortalRoute.First(UltimaThule, BaseOmicron, UltimaThule, leg.Portal.At, Portals, Teleports)!.Portal.Holder);
    }

    private static readonly HashSet<uint> SocietyZones = new() { UltimaThule, Elysion, Mare, Urqopacha, Zirgorteh };

    [Fact]
    public void FreeWarpsInAndOutOfTheSocietyZonesAreKept()
    {
        Assert.True(PortalRoute.Usable(Urqopacha, Zirgorteh, 0, false, SocietyZones));
        Assert.True(PortalRoute.Usable(Zirgorteh, Urqopacha, 0, false, SocietyZones));
        // Into a society zone from elsewhere (the way back in).
        Assert.True(PortalRoute.Usable(Tuliyollal, Urqopacha, 0, false, SocietyZones));
    }

    [Fact]
    public void PaidQuestOnlyAndFarAwayWarpsAreLeftOut()
    {
        // A ferry or an airship costs gil: its question must never get a Yes.
        Assert.False(PortalRoute.Usable(UltimaThule, UltimaThule, 80, false, SocietyZones));
        // A warp that shows only during a story quest (Kholusia's "Return to the soft-faced bloke?").
        Assert.False(PortalRoute.Usable(Mare, Mare, 0, true, SocietyZones));
        // Nowhere near a society's dailies.
        Assert.False(PortalRoute.Usable(129, 628, 0, false, SocietyZones));
    }

    [Fact]
    public void APortalThatDidntWorkIsLeftOut()
    {
        var broken = new HashSet<(uint, uint)> { (2013072u, 131483u) };
        Assert.Null(PortalRoute.First(UltimaThule, BaseOmicron, UltimaThule, Stigma4, Portals, Teleports, broken));
    }
}
