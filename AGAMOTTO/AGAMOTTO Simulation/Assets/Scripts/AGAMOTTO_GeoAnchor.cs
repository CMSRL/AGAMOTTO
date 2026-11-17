using System;
using UnityEngine;

/// <summary>
/// Maps RIDE scene X/Z to lat/lon using the first friendly spawn anchor (set from CoT).
/// </summary>
public static class AGAMOTTO_GeoAnchor
{
    const double MetersPerDegLat = 111_320.0;

    static bool _set;
    static double _anchorLat;
    static double _anchorLon;
    static double _anchorSceneX;
    static double _anchorSceneZ;

    public static bool IsSet => _set;

    public static void SetAnchor(double lat, double lon, double sceneX, double sceneZ)
    {
        _anchorLat = lat;
        _anchorLon = lon;
        _anchorSceneX = sceneX;
        _anchorSceneZ = sceneZ;
        _set = true;
        Debug.Log(
            $"AGAMOTTO_GeoAnchor: anchor lat={lat:F6} lon={lon:F6} scene=({sceneX:F1},{sceneZ:F1})");
    }

    public static bool TrySceneToLatLon(double sceneX, double sceneZ, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (!_set)
            return false;

        double metersPerDegLon = MetersPerDegLat * Math.Cos(_anchorLat * Math.PI / 180.0);
        if (Math.Abs(metersPerDegLon) < 1e-6)
            metersPerDegLon = MetersPerDegLat;

        double dx = sceneX - _anchorSceneX;
        double dz = sceneZ - _anchorSceneZ;
        lat = _anchorLat + dz / MetersPerDegLat;
        lon = _anchorLon + dx / metersPerDegLon;
        return true;
    }

    public static bool TryVector3ToLatLon(Vector3 world, out double lat, out double lon) =>
        TrySceneToLatLon(world.x, world.z, out lat, out lon);
}
