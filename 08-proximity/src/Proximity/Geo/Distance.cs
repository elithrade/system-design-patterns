namespace Proximity;

public static class Geo
{
    // PostGIS's mean Earth radius, as haversine_m in SQL uses.
    public const double EarthRadiusM = 6371008.7714;

    // Great-circle distance in meters.
    public static double DistanceM(double lat1, double lng1, double lat2, double lng2)
    {
        static double Rad(double deg) => deg * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Sqrt(a));
    }
}
