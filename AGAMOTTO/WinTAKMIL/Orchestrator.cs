
using Spyglass.Graphics;
using System;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using WinTak.Common;
using WinTak.Common.CoT;
using WinTak.Common.Utils;
using WinTak.CursorOnTarget.Services;
using WinTak.Framework.Preferences.Attributes;
using WinTak.Net.Chat;
using WinTak.Overlays.Services;
using WinTak.Overlays.ViewModels;

namespace AGAMOTTO.WinTAKMIL
{
    internal class Orchestrator
    {

        public int i = 10;
        public WinTak.Overlays.Services.IMapObjectItemManager _mapObjectItemManager { get; private set; }
        private CoTHelper _coTHelper { get; set; }
        string sAppPath;
        string filePath;

        public Orchestrator()
        {

            //_coTHelper = new CoTHelper("127.0.0.1", 65432);
            // sAppPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            //filePath = Path.Combine(sAppPath, "TAKOutput.csv");
        }


        public bool SendCurrentSituation(IMapObjectItemManager mapObjectItemManager, string userInputIP)
        {
            _coTHelper = new CoTHelper(userInputIP, 65432);
            _mapObjectItemManager = mapObjectItemManager;
            if (mapObjectItemManager.RootItems != null)
            {

                var testmessage = ExportMapObjectsToXml(mapObjectItemManager.RootItems);
                _coTHelper.SendCotMessageList(testmessage);


                return true;

            }
            else
            {
                return false;
            }
            
        }

        public static List<XDocument> ExportMapObjectsToXml(ICollection<MapObjectItem> rootItems)
        {
            
            var cotMessages = new List<XDocument>();
            foreach (var item in rootItems)
            {
                if(item != null)
                {
                    foreach (var child in item.Children)
                    {
                        if(child.MapItem != null)
                        {
                            string uid = child.MapItem.Uid ?? "none";
                            string type = child.MapItem.GetCotType() ?? "a-f-G-U-C"; // Default to combat unit if type is null
                            DateTime time = DateTime.UtcNow;
                            double? lat = child.Position.Latitude;
                            double? lon = child.Position.Longitude;
                            double? hae = child.Position.Altitude;

                            var cotEvent = new XElement("event",
                                new XAttribute("version", "2.0"),
                                new XAttribute("uid", uid),
                                new XAttribute("type", type),
                                new XAttribute("how", "m-g"),
                                new XAttribute("time", time.ToString("o")),
                                new XAttribute("start", time.ToString("o")),
                                new XAttribute("stale", time.AddMinutes(5).ToString("o")),
                                new XElement("point",
                                    new XAttribute("lat", lat),
                                    new XAttribute("lon", lon),
                                    new XAttribute("hae", hae),
                                    new XAttribute("ce", "30.0"),   // Customize error if needed
                                    new XAttribute("le", "30.0")
                                ),
                                new XElement("detail",
                                    new XElement("contact", new XAttribute("callsign", child.MapItem.Name)),
                                    new XElement("remarks", $"Generated from WinTAK at {time}")
                                )
                            );

                            cotMessages.Add(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), cotEvent));
                        }

                    }
                }



                
            }
            return cotMessages;
        }
    }
}