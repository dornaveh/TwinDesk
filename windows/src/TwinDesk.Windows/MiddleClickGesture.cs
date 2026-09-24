namespace TwinDesk;

internal enum MiddleAction { Down, Up, Toggle }

// Pure state: the hook owns routing/replay, and a UI timer calls Advance even
// when no more input arrives. A toggle never leaves a button held on either PC.
internal sealed class MiddleClickGesture(int interval, int horizontalSlop, int verticalSlop)
{
    internal static bool IsPhysical(uint flags) => (flags & 3) == 0;
    private enum Phase { Idle, FirstDown, FirstUp, SecondDown, Delivered, IgnoreUntilUp }
    private Phase phase;
    private bool pressed, coolingClick;
    private long deadline, cooldownUntil, dx, dy;
    internal bool Pending => phase is Phase.FirstDown or Phase.FirstUp or Phase.SecondDown;
    internal IReadOnlyList<MiddleAction> Advance(long now)
    {
        if (!Pending || now < deadline) return [];
        return Deliver();
    }
    private IReadOnlyList<MiddleAction> Deliver()
    {
        var previous = phase;
        phase = pressed ? Phase.Delivered : Phase.Idle;
        return previous switch {
            Phase.FirstDown => [MiddleAction.Down],
            Phase.FirstUp => [MiddleAction.Down, MiddleAction.Up],
            Phase.SecondDown => [MiddleAction.Down, MiddleAction.Up, MiddleAction.Down],
            _ => []
        };
    }
    internal IReadOnlyList<MiddleAction> Button(bool down, long now)
    {
        var result = new List<MiddleAction>(Advance(now));
        if (pressed == down) return result; // Repeated/orphan edges cannot create a gesture.
        pressed = down;
        if (phase == Phase.IgnoreUntilUp) { if (!down) phase = Phase.Idle; return result; }
        if (down)
        {
            if (phase == Phase.FirstUp) { phase = Phase.SecondDown; deadline = now + interval; }
            else if (now < cooldownUntil)
            { phase = Phase.Delivered; coolingClick = true; result.Add(MiddleAction.Down); }
            else { phase = Phase.FirstDown; coolingClick = false; dx = dy = 0; deadline = now + interval; }
        }
        else switch (phase)
        {
            case Phase.FirstDown: phase = Phase.FirstUp; break;
            case Phase.SecondDown:
                phase = Phase.Idle; cooldownUntil = now + interval;
                result.Add(MiddleAction.Toggle); break;
            case Phase.Delivered:
                phase = Phase.Idle; result.Add(MiddleAction.Up);
                if (coolingClick) cooldownUntil = now + interval;
                break;
        }
        return result;
    }
    internal IReadOnlyList<MiddleAction> Move(int x, int y, long now)
    {
        var actions = Advance(now);
        if (!Pending) return actions;
        dx += x; dy += y;
        if (Math.Abs(dx) > horizontalSlop || Math.Abs(dy) > verticalSlop) return Deliver();
        return actions;
    }
    internal IReadOnlyList<MiddleAction> Flush() => Pending ? Deliver() : [];
    internal IReadOnlyList<MiddleAction> Reset()
    {
        var release = phase == Phase.Delivered;
        phase = pressed ? Phase.IgnoreUntilUp : Phase.Idle;
        dx = dy = 0;
        // Retain the cooldown across the target change caused by the double click.
        return release ? [MiddleAction.Up] : [];
    }
}
