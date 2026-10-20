namespace TwinDesk;

public static class MouseInputPolicy
{
    public const nuint OwnMouseInputTag = 0x5457444D;

    public static bool ShouldProcess(uint message, uint flags, nuint extra, bool remote)
    {
        if (extra == OwnMouseInputTag) return false;
        if ((flags & 3) == 0) return true;

        // Mouse software can inject high-resolution wheel input. Forward that
        // wheel unchanged only while controlling the Mac. Keep lower-integrity
        // events and injected moves/buttons outside the gesture/forwarding path.
        return remote && flags == 1 && message is 0x20A or 0x20E;
    }
}
