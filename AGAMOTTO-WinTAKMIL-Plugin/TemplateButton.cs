using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.Composition;
using WinTak.Framework.Docking;
using WinTak.Framework.Tools;
using WinTak.Framework.Tools.Attributes;

namespace AGAMOTTO_WinTAK_Plugin
{
    [Button("Template_TemplateButton", "AGAMOTTO",
    LargeImage = "pack://application:,,,/AGAMOTTO WinTAK Plugin;component/assets/AGAMOTTO Logo v2 1.png",
    SmallImage = "pack://application:,,,/AGAMOTTO WinTAK Plugin;component/assets/AGAMOTTO Logo v2 1.png")]

    internal class TemplateButton : WinTak.Framework.Tools.Button
    {
        private WinTak.Framework.Docking.IDockingManager _dockingManager;

        [System.ComponentModel.Composition.ImportingConstructor]
        public TemplateButton(WinTak.Framework.Docking.IDockingManager dockingManager)
        {
            _dockingManager = dockingManager;
        }

        protected override void OnClick()
        {
            base.OnClick();

            var pane = _dockingManager.GetDockPane(TemplateDockPane.ID);
            pane?.Activate();
        }
    }
}
