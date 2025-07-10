using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using WinTak.Overlays.Services;
using WinTak.Overlays.ViewModels;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace AGAMOTTO.WinTAKMIL
{
    internal class Orchestrator
    {
        //update test
        public int i = 10;
        public WinTak.Overlays.Services.IMapObjectItemManager _mapObjectItemManager { get; private set; }
        string docPath;
        string filePath;

        public Orchestrator() 
        {
            /*var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                NewLine = Environment.NewLine,
            };

            docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            filePath = Path.Combine(docPath, "TAKOutput.csv");*/
        }


        public bool CreateCSV(IMapObjectItemManager mapObjectItemManager)
        {
            _mapObjectItemManager = mapObjectItemManager;
            if (mapObjectItemManager.RootItems != null)
            {
                ICollection<MapObjectItem> rootItems = _mapObjectItemManager.RootItems;
                if (rootItems != null)
                {
                    foreach (var name in rootItems)
                    {
                        //TestString += name.Id.ToString() + "\n";
                        //TestString += name.ChildCount.ToString() + "\n";
                        
                        using (var writer = new StreamWriter(filePath))
                        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
                        {
                            csv.WriteRecords((System.Collections.IEnumerable)name);
                        }

                        if (name.Id == "Hostile Markers")
                        {
                            foreach (var hostileMarker in name.Children)
                            {
                               // TestString += hostileMarker.MapItem.Name.ToString() + "\n";
                            }
                        }
                    }

                }

                
                return true;

            }
            else{
                return false;
            }
        }

    }
}