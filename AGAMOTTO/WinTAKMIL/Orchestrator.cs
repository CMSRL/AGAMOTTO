
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
//using System.Windows.Shapes;
using System.Xml.Linq;
using WinTak.Overlays.Services;
using WinTak.Overlays.ViewModels;

namespace AGAMOTTO.WinTAKMIL
{
    internal class Orchestrator
    {
        
        public int i = 10;
        public WinTak.Overlays.Services.IMapObjectItemManager _mapObjectItemManager { get; private set; }
        string sAppPath;
        string filePath;

        public Orchestrator() 
        {
            

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

                }

                
                return true;

            }
            else{
                return false;
            }
        }

    }
}