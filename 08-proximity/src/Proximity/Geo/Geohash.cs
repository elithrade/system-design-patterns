namespace Proximity;

// Stage 1, part 1: geohashes.
//
// A geohash splits the world in halves again and again, alternating longitude and latitude
// (longitude first), one bit per split: 1 for the upper half. Every 5 bits become one character
// of the alphabet below. Each character added makes the cell 32 times smaller, and a geohash's
// cell contains every longer geohash that starts with it: that is what makes a prefix query work.
//
// Encode must give the same string as PostGIS's ST_GeoHash(ST_SetSRID(ST_MakePoint(lng, lat), 4326), precision):
// the tests compare them on random points.
// Neighbors returns the 8 cells of the same precision that share an edge or a corner with the cell.
// Cells across the antimeridian or the poles do not come up in this lab (the data is in New York).
//
// Tests: dotnet test --filter-namespace "*Stage1"
public static class Geohash
{
    public const string Alphabet = "0123456789bcdefghjkmnpqrstuvwxyz";

    public static string Encode(double lat, double lng, int precision)
    {
        throw new NotImplementedException();
    }

    public static IReadOnlyList<string> Neighbors(string geohash)
    {
        throw new NotImplementedException();
    }
}
