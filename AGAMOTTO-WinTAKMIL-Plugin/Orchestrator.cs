using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WinTak.Common.CoT;
using WinTak.Common.Utils;
using WinTak.CursorOnTarget.Services;
using WinTak.Overlays.Services;
using WinTak.Overlays.ViewModels;

namespace AGAMOTTO.WinTAKMIL
{
    public enum DispatchMode { Evaluation, Training }

    internal class Orchestrator
    {

        public IMapObjectItemManager _mapObjectItemManager { get; private set; }
        private CoTHelper _coTHelper { get; set; }
        public DispatchMode CurrentMode { get; set; } = DispatchMode.Evaluation;

        public string CurrentMission { get; set; } = "Attack to Destroy";

        public Orchestrator()
        {
            _coTHelper = new CoTHelper("127.0.0.1", 65432);
        }

        /// <summary>Sets the TCP host/port used for the next send (Unity CoT listener on Play).</summary>
        public void ConfigureListener(string host, int port)
        {
            _coTHelper.SetEndpoint(host, port);
        }

        /// <summary>Synchronous send (blocks caller). Prefer <see cref="SendCurrentSituationAsync"/> from the UI.</summary>
        public bool SendCurrentSituation(IMapObjectItemManager mapObjectItemManager, string host, int port)
        {
            ConfigureListener(host, port);
            _mapObjectItemManager = mapObjectItemManager;
            if (_mapObjectItemManager?.RootItems == null) return false;

            var docs = ExportMapObjectsToXml(_mapObjectItemManager.RootItems, CurrentMode, CurrentMission);
            if (docs.Count == 0) return false;

            _coTHelper.SendCotMessageList(docs);
            return true;
        }

        /// <summary>Non-blocking send with async TCP I/O (recommended from WinTAK UI thread).</summary>
        public async Task<(bool ok, string error)> SendCurrentSituationAsync(
            IMapObjectItemManager mapObjectItemManager,
            string host,
            int port,
            CancellationToken cancellationToken = default)
        {
            ConfigureListener(host, port);
            _mapObjectItemManager = mapObjectItemManager;
            if (_mapObjectItemManager?.RootItems == null)
                return (false, "Map manager has no root items.");

            var docs = ExportMapObjectsToXml(_mapObjectItemManager.RootItems, CurrentMode, CurrentMission);
            if (docs.Count == 0)
                return (false, "No map objects exported to CoT.");

            return await _coTHelper.SendCotMessageListAsync(docs, cancellationToken).ConfigureAwait(false);
        }

        public bool SaveCurrentSituationToFolder(IMapObjectItemManager mapObjectItemManager, string outputFolder)
        {
            _mapObjectItemManager = mapObjectItemManager;
            if (_mapObjectItemManager?.RootItems == null) return false;

            var docs = ExportMapObjectsToXml(_mapObjectItemManager.RootItems, CurrentMode, CurrentMission);
            if (docs.Count == 0) return false;

            SaveDocsToFolder(docs, outputFolder);
            return true;
        }


        /// <summary>
        /// Looks at all map items and creates CoT events
        /// - Route: prefer Route.CotEvent, else build b-m-p-s-r with <detail><route><point/>
        /// - Non-route: basic point CoT
        /// </summary>
        public static List<XDocument> ExportMapObjectsToXml(ICollection<MapObjectItem> rootItems, DispatchMode mode, string mission)
        {
            var cotMessages = new List<XDocument>();
            var sentKeys = new HashSet<(string uid, string type)>(); //avoid duplicates
            var sentRouteUids = new HashSet<string>();
            var sentPointLatLon = new List<(double lat, double lon)>();

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
                        //cotMessages.Add(XDocument.Parse(cotEvtDoc.OuterXml));
                        var x = XDocument.Parse(cotEvtDoc.OuterXml);
                        Mode(x, mode, mission);
                        cotMessages.Add(x);
                        sentKeys.Add(key);
                        sentRouteUids.Add(uid);
                        continue;
                    }
                }

                // Otherwise build route event with verticies we can find
                var verts = TryExtractVertices(mapItem);
                if (verts != null && verts.Count >= 2)
                {
                    if (sentRouteUids.Contains(uid)) continue;

                    var rName = mapItem.Name ?? "Route";
                    var routeEvt = BuildRouteEvent(uid, rName, DateTime.UtcNow, verts);
                    //cotMessages.Add(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), routeEvt));
                    var x = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), routeEvt);
                    Mode(x, mode, mission);
                    cotMessages.Add(x);
                    sentKeys.Add((uid, "b-m-p-s-r"));
                    sentRouteUids.Add(uid);
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

                bool nearExistingPoint = false;
                foreach (var prev in sentPointLatLon)
                {
                    double dLat = (lat - prev.lat) * 111320.0;
                    double dLon = (lon - prev.lon) * 111320.0 * Math.Cos(lat * Math.PI / 180.0);
                    if (Math.Sqrt(dLat * dLat + dLon * dLon) < 40.0)
                    {
                        nearExistingPoint = true;
                        break;
                    }
                }
                if (nearExistingPoint)
                    continue;
                sentPointLatLon.Add((lat, lon));

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

                //cotMessages.Add(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), evt));
                var xpt = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), evt);
                Mode(xpt, mode, mission);
                cotMessages.Add(xpt);
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

        // CoT route builder (b-m-p-s-r + <detail><route>)
        internal static XElement BuildRouteEvent(
            string uid, string name, DateTime timestamp,
            List<(double lat, double lon, double? alt)> vertices,
            int? colorArgb = null)
        {
            var first = vertices[0];
            string tNow = timestamp.ToString("o");
            string tStale = timestamp.AddMinutes(15).ToString("o");

            var detail = new XElement("detail",
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
                ));

            if (colorArgb.HasValue)
            {
                detail.Add(new XElement("color", new XAttribute("argb", colorArgb.Value.ToString(CultureInfo.InvariantCulture))));
                detail.Add(new XElement("strokeColor", new XAttribute("value", colorArgb.Value.ToString(CultureInfo.InvariantCulture))));
            }

            return new XElement("event",
                new XAttribute("version", "2.0"),
                new XAttribute("uid", uid),
                new XAttribute("type", "b-m-p-s-r"),
                new XAttribute("how", "m-g"),
                new XAttribute("time", tNow),
                new XAttribute("start", tNow),
                new XAttribute("stale", tStale),
                new XElement("point",
                    new XAttribute("lat", first.lat.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("lon", first.lon.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("hae", (first.alt ?? 0.0).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("ce", "9999999"),
                    new XAttribute("le", "9999999")
                ),
                detail);
        }

        /// <summary>Blue policy route overlay for post-training inference (ARGB 0xFF0000FF).</summary>
        public static XDocument BuildBluePolicyRouteDocument(
            string routeUid,
            string routeName,
            IList<(double lat, double lon, double? alt)> vertices)
        {
            if (vertices == null || vertices.Count < 2)
                return null;
            var list = new List<(double, double, double?)>(vertices.Count);
            foreach (var v in vertices)
                list.Add(v);
            var evt = BuildRouteEvent(routeUid, routeName, DateTime.UtcNow, list, colorArgb: unchecked((int)0xFF0000FF));
            return new XDocument(evt);
        }

        public bool TryImportBluePolicyRoute(
            string routeUid,
            string routeName,
            IList<(double lat, double lon, double? alt)> vertices)
        {
            var doc = BuildBluePolicyRouteDocument(routeUid, routeName, vertices);
            if (doc == null)
                return false;
            return CotMapImporter.TryImportCotXml(doc.ToString());
        }

        // Legacy overload used by map export
        private static XElement BuildRouteEvent(
            string uid, string name, DateTime timestamp,
            List<(double lat, double lon, double? alt)> vertices)
            => BuildRouteEvent(uid, name, timestamp, vertices, null);

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

        private static void SaveDocsToFolder(List<XDocument> docs, string outputFolder)
        {
            Directory.CreateDirectory(outputFolder);

            foreach (var xdoc in docs)
            {
                var root = xdoc.Root;
                var uid = root?.Attribute("uid")?.Value ?? "unknown";
                var type = root?.Attribute("type")?.Value ?? "unknown";
                var ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");

                var safeUid = MakeSafe(uid, 48);
                var safeType = MakeSafe(type, 48);
                var fileName = $"{ts}_{safeType}_{safeUid}.xml";

                xdoc.Save(Path.Combine(outputFolder, fileName));
            }
        }

        private static string MakeSafe(string s, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(s)) return "none";
            var invalid = Path.GetInvalidFileNameChars();
            var filtered = new string(s.Where(c => !invalid.Contains(c)).ToArray());
            return filtered.Length > maxLen ? filtered.Substring(0, maxLen) : filtered;
        }

        public static void Mode(XDocument doc, DispatchMode mode, string mission)
        {
            if (doc?.Root == null) return;
            
            var ev = doc.Root;
            var val = mode.ToString();
            var miss = mission;

            ev.SetAttributeValue("mode", val);
            ev.SetAttributeValue("mission", miss);
        }
    }
}
