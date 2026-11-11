namespace VoiceSiteLauncher;

internal sealed class PushToTalk(Action onStart, Action onStop)
{
    private readonly HashSet<uint> _pressed = [];
    private bool _active;

    public void Handle(uint scanCode, bool down)
    {
        if (scanCode is not (0x2F or 0x40))
            return;
        if (down)
            _pressed.Add(scanCode);
        else
            _pressed.Remove(scanCode);
        if (!_active && _pressed.Count == 2)
        {
            _active = true;
            onStart();
        }
        else if (_active && _pressed.Count == 0)
        {
            _active = false;
            onStop();
        }
    }
}