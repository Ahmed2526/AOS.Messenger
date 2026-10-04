using AOS.Messenger.Domain.Common;

namespace AOS.Messenger.Tests;

/// <summary>
/// Sanity tests verifying the Domain layer compiles and core base types work correctly.
/// Real feature tests will be added in future phases alongside each feature.
/// </summary>
public class DomainSanityTests
{
    // A minimal concrete entity to exercise the base class.
    private sealed class SampleEntity : Entity<Guid>
    {
        public SampleEntity(Guid id) => Id = id;
    }

    // A minimal concrete value object.
    private sealed class SampleValue : ValueObject
    {
        public string Name { get; }
        public int Amount { get; }

        public SampleValue(string name, int amount)
        {
            Name = name;
            Amount = amount;
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Name;
            yield return Amount;
        }
    }

    [Fact]
    public void Entity_Id_IsAssigned()
    {
        var id = Guid.NewGuid();
        var entity = new SampleEntity(id);
        Assert.Equal(id, entity.Id);
    }

    [Fact]
    public void ValueObject_EqualWhenComponentsMatch()
    {
        var a = new SampleValue("hello", 42);
        var b = new SampleValue("hello", 42);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ValueObject_NotEqualWhenComponentsDiffer()
    {
        var a = new SampleValue("hello", 42);
        var b = new SampleValue("world", 42);
        Assert.NotEqual(a, b);
    }
}
