using Dalamud.Plugin;
using SphereMapPlus.Interop;
using SphereMapPlus.Spheremap;
using SphereMapPlus.UI;

namespace SphereMapPlus;

public sealed class SphereMapPlusPlugin : IDalamudPlugin
{
    public static SphereMapPlusPlugin P { get; private set; } = null!;

    private readonly IDalamudPluginInterface _pi;
    private readonly SlicePatchService _patch;
    private readonly TextureLibrary _library;
    private readonly SchemeAutoSwitch _autoSwitch;
    private readonly ProbeWindow _window;

    public SphereMapPlusPlugin(IDalamudPluginInterface pluginInterface, IPluginLog log, IClientState clientState,
        IFramework framework, IObjectTable objects)
    {
        P = this;
        _pi = pluginInterface;
        _patch = new SlicePatchService(pluginInterface, log, framework, clientState);
        _library = new TextureLibrary(pluginInterface, log);
        _autoSwitch = new SchemeAutoSwitch(pluginInterface, log, clientState, objects, _patch, _library);
        _window = new ProbeWindow(pluginInterface, log, clientState, _patch, _library, _autoSwitch);
        _pi.UiBuilder.Draw += _window.Draw;
        _pi.UiBuilder.OpenConfigUi += _window.Toggle;
        log.Info("SphereMapPlus v1.0.1 已加载");
    }

    public void Dispose()
    {
        _pi.UiBuilder.Draw -= _window.Draw;
        _pi.UiBuilder.OpenConfigUi -= _window.Toggle;
        _window.Dispose();
        _autoSwitch.Dispose();
        _patch.Dispose();
        if (P == this)
            P = null!;
    }
}
