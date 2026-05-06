using Autodesk.AutoCAD.Runtime;
using Callout.Services;

[assembly: ExtensionApplication(typeof(Callout.CalloutApp))]

namespace Callout
{
    public class CalloutApp : IExtensionApplication
    {
        public void Initialize()
        {
            CalloutWatcher.Initialize();
        }

        public void Terminate()
        {
            CalloutWatcher.Terminate();
        }
    }
}
