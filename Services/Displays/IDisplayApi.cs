namespace VNotch.Services.Displays;

/// <summary>Reads and changes Windows display settings; rejected changes return false.</summary>
public interface IDisplayApi
{
    IReadOnlyList<DisplayInfo> List();
    bool SetModes(IReadOnlyDictionary<string, DisplayMode> modes);
    bool SetPositions(IReadOnlyDictionary<string, Int32Point> topLefts);
    bool SetPrimary(string id);
    bool SetActive(string id, bool on);
    bool SetHdr(string id, bool on);
    bool SetZoom(string id, int percent);
    int? GetBrightness(string id);
    bool SetBrightness(string id, int percent);
}
