using System.IO;
using System.Linq;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class OriginHeartGoldItemDataTests
{
    [Fact]
    public void CatalogContains790NamedItems()
    {
        Assert.True(OriginHeartGoldItemData.IsAvailable);
        Assert.Equal(790, OriginHeartGoldItemData.AvailableItems.Count);

        Assert.True(OriginHeartGoldItemData.TryGet(4, out var ball));
        Assert.Equal("Poké Ball", ball.Name);
        Assert.Equal("balls", ball.Pocket);

        Assert.True(OriginHeartGoldItemData.TryGet(744, out var logger));
        Assert.Equal("Chain Logger", logger.Name);
        Assert.Equal("keyItems", logger.Pocket);

        Assert.True(OriginHeartGoldItemData.TryGet(745, out var allocator));
        Assert.Equal("EV Allocator", allocator.Name);
        Assert.Equal("keyItems", allocator.Pocket);
    }

    [Fact]
    public void RealOriginSessionProvidesCorrectItemNamesAndPouches()
    {
        var path = Path.Combine(
            @"C:\PKForgeSaves",
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv");

        using var session = new OriginHeartGoldEngineSession(
            File.ReadAllBytes(path), "teste");

        var names = session.GetItemNames();

        Assert.Equal("Poké Ball", names[4]);
        Assert.Equal("Chain Logger", names[744]);
        Assert.Equal("EV Allocator", names[745]);

        Assert.Contains(4, session.GetPouchLegalItems("Poké Balls"));
        Assert.Contains(744, session.GetPouchLegalItems("Key Items"));
        Assert.Contains(745, session.GetPouchLegalItems("Key Items"));

        foreach (var pouch in session.GetBag())
        {
            var legal = session.GetPouchLegalItems(pouch.Name);
            Assert.NotEmpty(legal);

            Assert.All(legal, id =>
                Assert.False(string.IsNullOrWhiteSpace(names[id])));
        }
    }
}