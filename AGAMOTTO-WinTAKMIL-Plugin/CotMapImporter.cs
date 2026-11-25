using System;
using System.Reflection;
using System.Xml.Linq;
using WinTak.Common.CoT;

namespace AGAMOTTO.WinTAKMIL
{
    /// <summary>
    /// Best-effort import of CoT XML onto the WinTAK map (reflection over SDK import services).
    /// </summary>
    internal static class CotMapImporter
    {
        public static bool TryImportCotXml(string cotXml, object cotService = null)
        {
            if (string.IsNullOrWhiteSpace(cotXml))
                return false;

            if (cotService != null && TryInvokeImporter(cotService, cotXml))
                return true;

            foreach (var service in ResolveImportServices())
            {
                if (TryInvokeImporter(service, cotXml))
                    return true;
            }

            return false;
        }

        static object[] ResolveImportServices()
        {
            var found = new System.Collections.Generic.List<object>();
            try
            {
                var slType = Type.GetType("WinTak.Framework.ServiceLocator, WinTak.Framework");
                if (slType == null)
                    return found.ToArray();
                var current = slType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (current == null)
                    return found.ToArray();
                var getInstance = current.GetType().GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Instance);
                if (getInstance == null)
                    return found.ToArray();

                foreach (var typeName in new[]
                {
                    "WinTak.Import.ICotImporter, WinTak.Import",
                    "WinTak.CursorOnTarget.Services.ICotImporter, WinTak.CursorOnTarget",
                    "WinTak.CursorOnTarget.Services.ICotEventProcessor, WinTak.CursorOnTarget",
                })
                {
                    var iface = Type.GetType(typeName);
                    if (iface == null)
                        continue;
                    try
                    {
                        var generic = getInstance.MakeGenericMethod(iface);
                        var svc = generic.Invoke(current, null);
                        if (svc != null)
                            found.Add(svc);
                    }
                    catch
                    {
                        /* optional service */
                    }
                }
            }
            catch
            {
                /* ServiceLocator unavailable */
            }

            return found.ToArray();
        }

        static bool TryInvokeImporter(object service, string cotXml)
        {
            if (service == null)
                return false;

            var t = service.GetType();
            object cotEvent = TryParseCotEvent(cotXml);

            foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name.IndexOf("Import", StringComparison.OrdinalIgnoreCase) < 0
                    && method.Name.IndexOf("Process", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var ps = method.GetParameters();
                if (ps.Length == 1)
                {
                    try
                    {
                        if (ps[0].ParameterType == typeof(string))
                        {
                            method.Invoke(service, new object[] { cotXml });
                            return true;
                        }

                        if (cotEvent != null && ps[0].ParameterType.IsInstanceOfType(cotEvent))
                        {
                            method.Invoke(service, new object[] { cotEvent });
                            return true;
                        }

                        if (ps[0].ParameterType == typeof(XDocument))
                        {
                            method.Invoke(service, new object[] { XDocument.Parse(cotXml) });
                            return true;
                        }
                    }
                    catch
                    {
                        /* try next overload */
                    }
                }
            }

            return false;
        }

        static object TryParseCotEvent(string cotXml)
        {
            try
            {
                var cotType = typeof(CotEvent);
                foreach (var method in cotType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(method.Name, "Parse", StringComparison.Ordinal))
                        continue;
                    var ps = method.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        return method.Invoke(null, new object[] { cotXml });
                }
            }
            catch
            {
                /* fall through */
            }

            return null;
        }
    }
}
