namespace TwinDesk;

public sealed record MicrophoneDevice(string Id, string Name, string ContainerId = "")
{
    public override string ToString() => Name;
}

// Endpoint IDs can change when Windows rebuilds a device. Never fall back to
// the first/default microphone, or choose among ambiguous matching devices.
public static class MicrophoneSelection
{
    public static MicrophoneDevice? Resolve(IEnumerable<MicrophoneDevice> inputs,
        string id, string name, string containerId)
    {
        var devices = inputs.ToArray();
        var exact = devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        if (!string.IsNullOrWhiteSpace(containerId))
        {
            var sameHardware = devices.Where(d => string.Equals(d.ContainerId, containerId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameHardware.Length == 1) return sameHardware[0];
            return UniqueNamed(sameHardware, name);
        }
        return UniqueNamed(devices, name);
    }

    private static MicrophoneDevice? UniqueNamed(IEnumerable<MicrophoneDevice> devices, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "Unknown") return null;
        var matches = devices.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
