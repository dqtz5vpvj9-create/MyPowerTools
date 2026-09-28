namespace MyPowerTools.Android.Input;

/// <summary>Contacts whose down event reached the view and have not ended or been cancelled.</summary>
internal sealed class ActiveTouchSequences
{
    private readonly HashSet<int> _contacts = [];

    public int Count => _contacts.Count;
    public void Begin(int pointerId) => _contacts.Add(pointerId);
    public void End(int pointerId) => _contacts.Remove(pointerId);

    public int[] TakeForCancellation()
    {
        var contacts = _contacts.ToArray();
        // Clear before dispatch: cancellation can synchronously navigate or pause the activity again.
        _contacts.Clear();
        return contacts;
    }
}
