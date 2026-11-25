namespace ScalingWrites.Tests.Stage3;

// Stage 3 goal, part 1: a ring that spreads keys evenly and moves few of them when nodes change.
// No database here.
public sealed class RingTests
{
    private const int Keys = 100_000;
    private static readonly string[] Three = ["p0", "p1", "p2"];

    [Fact]
    public void A_key_maps_to_the_same_node_whatever_the_node_order()
    {
        var a = new ConsistentHashRing(Three, 100);
        var b = new ConsistentHashRing(["p2", "p0", "p1"], 100);

        for (long key = 1; key <= 10_000; key++)
        {
            Assert.Equal(a.NodeFor(key), b.NodeFor(key));
            Assert.Equal(a.NodeFor(key), a.NodeFor(key));
        }
    }

    [Fact]
    public void Keys_split_evenly_over_three_nodes()
    {
        var ring = new ConsistentHashRing(Three, 100);

        var counts = Count(ring);

        Assert.Equal(3, counts.Count);
        foreach (var (node, count) in counts)
        {
            Assert.True(count is > Keys * 23 / 100 and < Keys * 43 / 100, $"{node} has {count} of {Keys} keys");
        }
    }

    [Fact]
    public void Adding_a_fourth_node_moves_about_a_quarter_of_the_keys_all_to_it()
    {
        var before = new ConsistentHashRing(Three, 100);
        var after = new ConsistentHashRing([.. Three, "p3"], 100);

        var moved = 0;
        for (long key = 1; key <= Keys; key++)
        {
            var to = after.NodeFor(key);
            if (to != before.NodeFor(key))
            {
                moved++;
                Assert.Equal("p3", to);
            }
        }

        Assert.True(moved is > Keys * 15 / 100 and < Keys * 35 / 100, $"{moved} of {Keys} keys moved");
    }

    [Fact]
    public void Removing_a_node_moves_only_its_keys()
    {
        var before = new ConsistentHashRing(Three, 100);
        var after = new ConsistentHashRing(["p0", "p2"], 100);

        for (long key = 1; key <= Keys; key++)
        {
            var was = before.NodeFor(key);
            if (was != "p1")
            {
                Assert.Equal(was, after.NodeFor(key));
            }
        }
    }

    private static Dictionary<string, int> Count(ConsistentHashRing ring)
    {
        var counts = new Dictionary<string, int>();
        for (long key = 1; key <= Keys; key++)
        {
            var node = ring.NodeFor(key);
            counts[node] = counts.GetValueOrDefault(node) + 1;
        }
        return counts;
    }
}
