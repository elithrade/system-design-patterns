namespace ScalingWrites;

// Stage 3. A consistent hash ring: each node owns virtualNodes points on a ring of 64-bit hashes,
// and a key belongs to the first point at or after its own hash, wrapping around at the end.
//
// The goal, which the tests check:
//   - a key always maps to the same node, whatever order the nodes were given in
//   - with 3 nodes and 100 virtual nodes each, 100,000 keys split evenly (each node within 33% ± 7%)
//   - adding a fourth node moves about a quarter of the keys, and every key that moves goes to the
//     new node; removing a node moves only that node's keys
//
// Hash with StableHash.Of: string.GetHashCode differs between processes. Hash the key with
// StableHash.Of(key), and each point with something like StableHash.Of($"{node}#{i}").
// NodeFor is called on every request: build the ring once, here, and make the lookup O(log n).
//
// Tests: dotnet test --filter-namespace "*Stage3"
public sealed class ConsistentHashRing
{
    public ConsistentHashRing(IEnumerable<string> nodes, int virtualNodes)
    {
        throw new NotImplementedException();
    }

    public string NodeFor(long key)
    {
        throw new NotImplementedException();
    }
}
