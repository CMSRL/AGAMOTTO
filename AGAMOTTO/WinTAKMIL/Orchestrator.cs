using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinTak.Overlays.Services;
using WinTak.Overlays.ViewModels;

namespace AGAMOTTO.WinTAKMIL
{
    internal class Orchestrator
    {
        public int i = 10;
        public WinTak.Overlays.Services.IMapObjectItemManager _mapObjectItemManager { get; private set; }
        public Orchestrator() { }

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