using Autodesk.AutoCAD.Runtime;
using Callout.Logic;

namespace Callout
{
    public class CalloutCommand
    {
        [CommandMethod("CT", CommandFlags.Modal)]
        public void CreateCallout()
        {
            CalloutCoreLogic.ExecuteCallout(false);
        }

        [CommandMethod("CT1", CommandFlags.Modal)]
        public void CreateCalloutFar()
        {
            CalloutCoreLogic.ExecuteCallout(true);
        }

        [CommandMethod("CT2", CommandFlags.Modal)]
        public void ConfigureCallout()
        {
            CalloutCoreLogic.ConfigureCallout();
        }

        [CommandMethod("CTS", CommandFlags.Modal)]
        public void ShowCalloutStatus()
        {
            CalloutCoreLogic.ShowCalloutStatus();
        }

        [CommandMethod("CTFIXCROP", CommandFlags.Modal)]
        public void RepairCalloutCrops()
        {
            CalloutCoreLogic.RepairCalloutCrops();
        }
    }
}
