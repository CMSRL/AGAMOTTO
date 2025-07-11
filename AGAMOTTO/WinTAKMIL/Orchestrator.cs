
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

//using System.Windows.Shapes;
using System.Xml.Linq;
using WinTak.Common.CoT;
using WinTak.CursorOnTarget.Services;
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

            _coTHelper = new CoTHelper("127.0.0.1", 65432);
            sAppPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            filePath = Path.Combine(sAppPath, "TAKOutput.csv");
        }


        public bool CreateCSV(IMapObjectItemManager mapObjectItemManager)
        {
            _mapObjectItemManager = mapObjectItemManager;
            if (mapObjectItemManager.RootItems != null)
            {
                ICollection<MapObjectItem> rootItems = _mapObjectItemManager.RootItems;
                if (rootItems != null)
                {


                    using (var w = new StreamWriter(filePath))
                    {
                        foreach (var name in rootItems)
                        {
                            var first = name.Id.ToString();
                            //var second = name.MapItem.Name.ToString();
                            var line = string.Format("{0}", first);
                            w.WriteLine(line);
                            w.Flush();
                        }
                    }


                    //if (name.Id == "Hostile Markers")
                    // {
                    //    foreach (var hostileMarker in name.Children)
                    //    {
                    // TestString += hostileMarker.MapItem.Name.ToString() + "\n";
                    //    }
                    // }
                    string cotMessage = @"<event version='2.0' uid='tcp-1234' type='a-f-G-U-C' time='2025-07-11T09:45:00Z' start='2025-07-11T09:45:00Z' stale='2025-07-11T09:50:00Z' how='m-g'>
                        <point lat='41.3739' lon='-73.9159' hae='0' ce='9999999' le='9999999'/>
                        <detail>
                        <contact callsign='TCPUnit'/>
                        </detail>
                        </event>";
                    _coTHelper.SendCotMessage(cotMessage);
                }

                
                return true;

            }
            else{
                return false;
            }
        }

    }
}