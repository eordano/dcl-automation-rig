// NoopBrowser — an IBrowser that does nothing. The automation bridge is driven
// programmatically (Python / Claude / rig CDP harness), so it must NOT try to launch
// Chrome on Start() — and ProcessBrowser uses Mono APIs unavailable under IL2CPP anyway.
using CDPBridges;

namespace DCL.Automation
{
    public sealed class NoopBrowser : IBrowser
    {
        public BrowserOpenResult OpenUrl(string url) => BrowserOpenResult.Success();
    }
}
