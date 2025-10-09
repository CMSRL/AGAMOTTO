using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using WinTak.Common.CoT;
using WinTak.Common.Utils;
using WinTak.CursorOnTarget.Services;
using WinTak.Overlays.Services;
using WinTak.Overlays.ViewModels;

namespace AGAMOTTO.WinTAKMIL
{
    internal class Orchestrator
    {
        public IMapObjectItemManager _mapObjectItemManager { get; private set; }
        private CoTHelper _coTHelper { get; set; }

        public Orchestrator()
        {
            _coTHelper = new CoTHelper("127.0.0.1", 65432);
        }

        public bool SendCurrentSituation(IMapObjectItemManager mapObjectItemManager, string userInputIP)
        {
            _mapObjectItemManager = mapObjectItemManager;
            if (_mapObjectItemManager?.RootItems == null) return false;

            var docs = ExportMapObjectsToXml(_mapObjectItemManager.RootItems);
            if (docs.Count == 0) return false;

            _coTHelper.SendCotMessageList(docs);
            return true;
        }

        /// <summary>
        /// Looks at all map items and creates CoT events
        /// - Route: prefer Route.CotEvent, else build b-m-r with <detail><route><point/>
        /// - Non-route: basic point CoT
        /// </summary>
        public static List<XDocument> ExportMapObjectsToXml(ICollection<MapObjectItem> rootItems)
        {
            var cotMessages = new List<XDocument>();
            var sentKeys = new HashSet<(string uid, string type)>(); //avoid duplicates

            foreach (var node in EnumerateAllNodes(rootItems))
            {
                if (node?.MapItem == null) continue;
                var mapItem = node.MapItem;

                var uid = mapItem.Uid ?? "none";
                var type = mapItem != null ? mapItem.GetCotType() : null;
                if (string.IsNullOrWhiteSpace(type))
                    type = GetCotTypeFallback(mapItem) ?? "a-f-G-U-C";

                var key = (uid, type);
                if (sentKeys.Contains(key)) continue;

                // Try and use SDK's CoT if available
                var routeObj = GetProp(mapItem, "Route");
                if (routeObj != null)
                {
                    var cotEvtDoc = GetProp(routeObj, "CotEvent") as System.Xml.XmlDocument;
                    if (cotEvtDoc?.DocumentElement != null)
                    {
                        cotMessages.Add(XDocument.Parse(cotEvtDoc.OuterXml));
                        sentKeys.Add(key);
                        continue;
                    }
                }

                // Otherwise build route event with verticies we can find
                var verts = TryExtractVertices(mapItem);
                if (verts != null && verts.Count >= 2)
                {
                    var rName = mapItem.Name ?? "Route";
                    var routeEvt = BuildRouteEvent(uid, rName, DateTime.UtcNow, verts);
                    cotMessages.Add(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), routeEvt));
                    sentKeys.Add(key);
                    continue;
                }

                // Default points (e.g., entity on map)
                var t = DateTime.UtcNow;

                double lat = 0, lon = 0, alt = 0;
                try
                {
                    lat = node?.Position?.Latitude ?? 0.0;
                    lon = node?.Position?.Longitude ?? 0.0;
                    alt = node?.Position?.Altitude ?? 0.0;
                    if (double.IsNaN(alt)) alt = 0.0;
                }
                catch { /* some items may not expose Position */ }

                var evt = new XElement("event",
                    new XAttribute("version", "2.0"),
                    new XAttribute("uid", uid),
                    new XAttribute("type", type),
                    new XAttribute("how", "m-g"),
                    new XAttribute("time", t.ToString("o")),
                    new XAttribute("start", t.ToString("o")),
                    new XAttribute("stale", t.AddMinutes(5).ToString("o")),
                    new XElement("point",
                        new XAttribute("lat", lat.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("lon", lon.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("hae", alt.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("ce", "30.0"),
                        new XAttribute("le", "30.0")
                    ),
                    new XElement("detail",
                        new XElement("contact", new XAttribute("callsign", mapItem.Name)),
                        new XElement("remarks", $"Generated from WinTAK at {t}")
                    )
                );

                cotMessages.Add(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), evt));
                sentKeys.Add(key);
            }

            return cotMessages;
        }
        private static IEnumerable<MapObjectItem> EnumerateAllNodes(ICollection<MapObjectItem> roots)
        {
            if (roots == null) yield break;
            foreach (var n in roots)
            {
                if (n == null) continue;
                yield return n;

                var children = n.Children;
                if (children == null) continue;

                foreach (var c in EnumerateAllNodes(children))
                    yield return c;
            }
        }

        // Grab vertices for route
        // Try Route.Verticies -> Waypoints -> Geometry/Graphic
        private static List<(double lat, double lon, double? alt)> TryExtractVertices(object mapItem)
        {
            if (mapItem == null) return null;

            // RouteMapObject.Route.*
            var routeObj = GetProp(mapItem, "Route");
            if (routeObj != null)
            {
                var seq =
                      AsEnumerable(routeObj, "Verticies")
                   ?? AsEnumerable(routeObj, "Vertices")
                   ?? AsEnumerable(routeObj, "Points")
                   ?? AsEnumerable(routeObj, "Nodes")
                   ?? AsEnumerable(routeObj, "Path")
                   ?? AsEnumerable(routeObj, "Waypoints");

                if (seq == null)
                {
                    var rGeom = GetProp(routeObj, "Geometry");
                    if (rGeom != null)
                    {
                        seq =
                              AsEnumerable(rGeom, "Coordinates")
                           ?? AsEnumerable(rGeom, "Points")
                           ?? AsEnumerable(rGeom, "Vertices");
                    }
                }

                var vertsFromRoute = ExtractLatLonList(seq);
                if (vertsFromRoute != null && vertsFromRoute.Count >= 2) return vertsFromRoute;
            }

            // MapItem.Waypoints[*]
            var wpsEnum = AsEnumerable(mapItem, "Waypoints");
            if (wpsEnum != null)
            {
                var fromWps = new List<(double, double, double?)>();
                foreach (var wp in wpsEnum)
                {
                    if (wp == null) continue;

                    var pos = GetProp(wp, "Position");
                    var got = TryGetLatLonAltFromObject(pos);
                    if (got.HasValue) { fromWps.Add(got.Value); continue; }

                    var loc = GetProp(wp, "Location") ?? GetProp(wp, "Coordinate") ?? GetProp(wp, "Center");
                    got = TryGetLatLonAltFromObject(loc);
                    if (got.HasValue) { fromWps.Add(got.Value); continue; }

                    got = TryGetLatLonAltFromObject(wp);
                    if (got.HasValue) { fromWps.Add(got.Value); continue; }
                }
                if (fromWps.Count >= 2) return fromWps;
            }

            // Generic lists on the item
            System.Collections.IEnumerable seqDirect =
                  AsEnumerable(mapItem, "Verticies")
               ?? AsEnumerable(mapItem, "Vertices")
               ?? AsEnumerable(mapItem, "RoutePoints")
               ?? AsEnumerable(mapItem, "Waypoints");

            var verts = ExtractLatLonList(seqDirect);
            if (verts != null && verts.Count >= 2) return verts;

            // Graphic branch which gives us all inner vertices, not just start/end
            var graphic = GetProp(mapItem, "Graphic");
            if (graphic != null)
            {
                var path = GetProp(graphic, "Path");
                var seqG =
                      AsEnumerable(path, "Points")
                   ?? AsEnumerable(path, "Vertices")
                   ?? AsEnumerable(path, "_points")
                   ?? AsEnumerable(graphic, "_points")
                   ?? AsEnumerable(graphic, "Vertices")
                   ?? AsEnumerable(graphic, "Points");

                verts = ExtractLatLonList(seqG);
                if (verts != null && verts.Count >= 2) return verts;
            }

            // Geometry as a fallback to graphic search
            var geom = GetProp(mapItem, "Geometry");
            if (geom != null)
            {
                var seqGeo =
                      AsEnumerable(geom, "Coordinates")
                   ?? AsEnumerable(geom, "Points")
                   ?? AsEnumerable(geom, "Vertices");

                var points = GetProp(geom, "Points");
                var items = GetProp(points, "Items");
                seqGeo = seqGeo ?? (items as System.Collections.IEnumerable);

                verts = ExtractLatLonList(seqGeo);
                if (verts != null && verts.Count >= 2) return verts;
            }

            return null;
        }

        // CoT route builder (b-m-r + <detail><route>)
        private static XElement BuildRouteEvent(
            string uid, string name, DateTime timestamp,
            List<(double lat, double lon, double? alt)> vertices)
        {
            var first = vertices[0];
            string tNow = timestamp.ToString("o");
            string tStale = timestamp.AddMinutes(15).ToString("o");

            return new XElement("event",
                new XAttribute("version", "2.0"),
                new XAttribute("uid", uid),
                new XAttribute("type", "b-m-r"),
                new XAttribute("how", "m-g"),
                new XAttribute("time", tNow),
                new XAttribute("start", tNow),
                new XAttribute("stale", tStale),
                // Anchor the route at the first vertex
                new XElement("point",
                    new XAttribute("lat", first.lat.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("lon", first.lon.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("hae", (first.alt ?? 0.0).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("ce", "9999999"),
                    new XAttribute("le", "9999999")
                ),
                new XElement("detail",
                    new XElement("contact", new XAttribute("callsign", name ?? "Route")),
                    new XElement("route",
                        new XAttribute("name", name ?? "Route"),
                        vertices.Select(v =>
                        {
                            var el = new XElement("point",
                                new XAttribute("lat", v.lat.ToString(CultureInfo.InvariantCulture)),
                                new XAttribute("lon", v.lon.ToString(CultureInfo.InvariantCulture))
                            );
                            if (v.alt.HasValue)
                                el.Add(new XAttribute("hae", v.alt.Value.ToString(CultureInfo.InvariantCulture)));
                            return el;
                        })
                    )
                )
            );
        }

        // Build coords from discovered points
        private static List<(double lat, double lon, double? alt)> ExtractLatLonList(System.Collections.IEnumerable seq)
        {
            if (seq == null) return null;
            var list = new List<(double, double, double?)>();
            foreach (var v in seq)
            {
                var got = TryGetLatLonAltFromObject(v);
                if (got.HasValue) list.Add(got.Value);
            }
            return list.Count > 0 ? list : null;
        }

        // Suppport for different shnapes
        private static (double, double, double?)? TryGetLatLonAltFromObject(object obj)
        {
            if (obj == null) return null;

            var lat = GetDouble(obj, "Lat") ?? GetDouble(obj, "Latitude") ?? GetDouble(obj, "Y");
            var lon = GetDouble(obj, "Lon") ?? GetDouble(obj, "Longitude") ?? GetDouble(obj, "X");
            var alt = GetDouble(obj, "Alt") ?? GetDouble(obj, "Altitude") ?? GetDouble(obj, "HAE") ?? GetDouble(obj, "Z");

            if (lat.HasValue && lon.HasValue)
                return (lat.Value, lon.Value, alt);

            // coords are sometimes within nested position/location/etc...
            foreach (var pname in new[] { "Position", "Location", "Coordinate", "Center" })
            {
                var sub = GetProp(obj, pname);
                if (sub == null) continue;

                lat = GetDouble(sub, "Lat") ?? GetDouble(sub, "Latitude") ?? GetDouble(sub, "Y");
                lon = GetDouble(sub, "Lon") ?? GetDouble(sub, "Longitude") ?? GetDouble(sub, "X");
                alt = GetDouble(sub, "Alt") ?? GetDouble(sub, "Altitude") ?? GetDouble(sub, "HAE") ?? GetDouble(sub, "Z");

                if (lat.HasValue && lon.HasValue)
                    return (lat.Value, lon.Value, alt);
            }

            return null;
        }

        private static object GetProp(object obj, string name) =>
            obj?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);

        private static System.Collections.IEnumerable AsEnumerable(object obj, string name) =>
            GetProp(obj, name) as System.Collections.IEnumerable;

        private static double? GetDouble(object obj, string name)
        {
            var p = obj?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return null;
            var val = p.GetValue(obj);
            if (val == null) return null;
            try { return Convert.ToDouble(val, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string GetCotTypeFallback(object mapItem)
        {
            if (mapItem == null) return null;

            var m = mapItem.GetType().GetMethod("GetCotType", BindingFlags.Public | BindingFlags.Instance);
            if (m != null)
            {
                try
                {
                    var val = m.Invoke(mapItem, null) as string;
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
                catch { }
            }

            foreach (var propName in new[] { "CotType", "CoTType", "COTType", "Type" })
            {
                var p = mapItem.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (p == null) continue;
                try
                {
                    var s = p.GetValue(mapItem) as string;
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                catch { }
            }

            return null;
        }
    }
}
