namespace Proximity;

public enum Engine
{
    // the baseline: compute the distance to every driver
    Scan,
    // stage 1: a geohash column and a prefix query
    Geohash,
    // stage 2: PostGIS geography and a GiST index
    PostGis,
    // stages 3-4: Redis geo commands
    Redis,
}

// The "Proximity" section. Set from the command line with Proximity__Engine=PostGis etc.
public sealed class ProximityOptions
{
    public const string Section = "Proximity";

    public Engine Engine { get; set; } = Engine.Scan;

    // Stages 3-4: Redis keys start with this.
    public string KeyPrefix { get; set; } = "lab8:";

    // Stage 4: a driver who has not reported for this long is not offered to riders.
    public int StaleSeconds { get; set; } = 30;
}
