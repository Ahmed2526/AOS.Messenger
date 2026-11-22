using AOS.Messenger.Application.Common;

namespace AOS.Messenger.Tests;

public class ApplicationTests
{
    [Fact]
    public void NotFoundException_WithDirectMessage_SetsMessage()
    {
        var ex = new NotFoundException("Custom not found message");
        Assert.Equal("Custom not found message", ex.Message);
    }

    [Fact]
    public void NotFoundException_WithEntityAndKey_FormatsMessage()
    {
        var id = Guid.NewGuid();
        var ex = new NotFoundException("Conversation", id);
        Assert.Contains("Conversation", ex.Message);
        Assert.Contains(id.ToString(), ex.Message);
    }

    [Fact]
    public void ForbiddenException_DefaultMessage_IsAccessDenied()
    {
        var ex = new ForbiddenException();
        Assert.Equal("Access denied.", ex.Message);
    }

    [Fact]
    public void ForbiddenException_CustomMessage_SetsMessage()
    {
        var ex = new ForbiddenException("Not a participant.");
        Assert.Equal("Not a participant.", ex.Message);
    }

    [Fact]
    public void ConflictException_WithDirectMessage_SetsMessage()
    {
        var ex = new ConflictException("Idempotency conflict detected.");
        Assert.Equal("Idempotency conflict detected.", ex.Message);
    }

    [Fact]
    public void ConflictException_WithInnerException_SetsMessageAndInner()
    {
        var inner = new InvalidOperationException("DB unique constraint violation");
        var ex = new ConflictException("Concurrent creation conflict", inner);
        Assert.Equal("Concurrent creation conflict", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
