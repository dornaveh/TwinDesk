using TwinDesk;

var count = 0;
void Check(uint message, uint flags, nuint extra, bool remote, bool expected, string name)
{
    if (MouseInputPolicy.ShouldProcess(message, flags, extra, remote) != expected) throw new Exception("FAIL: " + name);
    count++; Console.WriteLine("PASS: " + name);
}
foreach (var message in new uint[] { 0x20A, 0x20E })
{
    var axis = message == 0x20A ? "vertical" : "horizontal";
    Check(message, 0, 0, true, true, $"Physical {axis} wheel reaches remote forwarding");
    Check(message, 1, 0, true, true, $"Normal injected {axis} wheel reaches remote forwarding");
    Check(message, 1, 123, true, true, $"Vendor extra info does not discard {axis} wheel");
    Check(message, 0, MouseInputPolicy.OwnMouseInputTag, true, false, $"Own-tag physical-looking {axis} wheel is excluded");
    Check(message, 1, MouseInputPolicy.OwnMouseInputTag, true, false, $"Own-tag injected {axis} wheel cannot loop");
    Check(message, 2, 0, true, false, $"Lower-integrity {axis} wheel is excluded");
    Check(message, 3, 0, true, false, $"Injected lower-integrity {axis} wheel is excluded");
    Check(message, 5, 0, true, false, $"Unknown injected flags are not newly accepted for {axis} wheel");
    Check(message, 1, 0, false, false, $"PC mode leaves injected {axis} wheel on the normal Windows path");
    Check(message, 0, 0, false, true, $"PC physical {axis} wheel retains existing processing");
}
foreach (var message in new uint[] { 0x200, 0x207, 0x208, 0x20B, 0x20C })
{
    Check(message, 1, 0, true, false, $"Injected non-wheel 0x{message:X} stays outside gesture handling");
    Check(message, 0, 0, true, true, $"Physical non-wheel 0x{message:X} keeps existing handling");
}
Console.WriteLine($"PASS: {count} wheel policy checks; no system hooks or synthesized input used.");
