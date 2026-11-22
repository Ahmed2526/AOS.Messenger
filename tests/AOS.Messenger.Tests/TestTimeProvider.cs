namespace AOS.Messenger.Tests;

public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset? initialUtcNow = null)
    {
        _utcNow = initialUtcNow ?? DateTimeOffset.UtcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan timeSpan)
    {
        _utcNow = _utcNow.Add(timeSpan);
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }
}
