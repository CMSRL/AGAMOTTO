using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGAMOTTO_WinTAK_Plugin
{
    [Prism.Mef.Modularity.ModuleExport(typeof(TemplateModule), InitializationMode = Prism.Modularity.InitializationMode.WhenAvailable)]
    internal class TemplateModule : Prism.Modularity.IModule
    {
        private readonly Prism.Events.IEventAggregator _eventAggregator;

        [System.ComponentModel.Composition.ImportingConstructor]
        public TemplateModule(Prism.Events.IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
        }
        // Modules will be initialized during startup. Any work that needs to be done at startup can
        // be initiated from here.
        public void Initialize()
        {

        }
    }
}
