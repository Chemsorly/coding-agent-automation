using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class JobIdTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        JobId id = "job-123";

        id.Value.Should().Be("job-123");
    }

    [Fact]
    public void ImplicitConversion_FromNull_ThrowsArgumentNullException()
    {
        var act = () => { JobId id = (string)null!; };

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var id = new JobId("job-456");

        id.ToString().Should().Be("job-456");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var id1 = new JobId("same-job");
        var id2 = new JobId("same-job");

        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var id1 = new JobId("job-a");
        var id2 = new JobId("job-b");

        id1.Should().NotBe(id2);
        (id1 != id2).Should().BeTrue();
    }

    [Fact]
    public void Equality_ImplicitConversion_MatchesExplicitConstruction()
    {
        JobId implicit1 = "job-1";
        var explicit1 = new JobId("job-1");

        implicit1.Should().Be(explicit1);
    }

    [Fact]
    public void Default_HasNullValue()
    {
        var id = default(JobId);

        id.Value.Should().BeNull();
    }

    [Fact]
    public void HashSet_WorksCorrectly()
    {
        var set = new HashSet<JobId>
        {
            new JobId("job-1"),
            new JobId("job-2")
        };

        set.Should().HaveCount(2);
        set.Contains(new JobId("job-1")).Should().BeTrue();
        set.Contains(new JobId("job-3")).Should().BeFalse();
    }

    [Fact]
    public void HashSet_Deduplicates_SameValues()
    {
        var set = new HashSet<JobId>
        {
            "job-1",
            "job-1" // duplicate via implicit conversion
        };

        set.Should().HaveCount(1);
    }
}

/// <summary>
/// Tests for the MessagePack formatter that serializes <see cref="JobId"/> as a bare string.
/// </summary>
public class JobIdFormatterTests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                new IMessagePackFormatter[] { new JobIdFormatter() },
                new IFormatterResolver[] { ContractlessStandardResolverAllowPrivate.Instance }));

    [Fact]
    public void RoundTrip_SerializesAsString_DeserializesBackToJobId()
    {
        var original = new JobId("abc-123");

        var bytes = MessagePackSerializer.Serialize(original, Options);
        var deserialized = MessagePackSerializer.Deserialize<JobId>(bytes, Options);

        deserialized.Should().Be(original);
        deserialized.Value.Should().Be("abc-123");
    }

    [Fact]
    public void Serialize_ProducesPlainString_NotMapFormat()
    {
        var id = new JobId("test-job");

        var bytes = MessagePackSerializer.Serialize(id, Options);
        // Deserialize as a raw string to prove it's stored as a bare string
        var asString = MessagePackSerializer.Deserialize<string>(bytes, Options);

        asString.Should().Be("test-job");
    }

    [Fact]
    public void Serialize_DefaultJobId_ThrowsMessagePackSerializationException()
    {
        var defaultId = default(JobId);

        var act = () => MessagePackSerializer.Serialize(defaultId, Options);

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public void Deserialize_FromPlainString_ProducesJobId()
    {
        // Serialize a raw string, then deserialize as JobId
        var bytes = MessagePackSerializer.Serialize("raw-string-job", Options);
        var deserialized = MessagePackSerializer.Deserialize<JobId>(bytes, Options);

        deserialized.Value.Should().Be("raw-string-job");
    }

    [Fact]
    public void Deserialize_FromNilToken_ThrowsMessagePackSerializationException()
    {
        // Serialize a null string to produce a nil MessagePack token
        var bytes = MessagePackSerializer.Serialize((string?)null, Options);

        var act = () => MessagePackSerializer.Deserialize<JobId>(bytes, Options);

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public void RoundTrip_GuidFormatJobId()
    {
        var guid = Guid.NewGuid().ToString();
        var original = new JobId(guid);

        var bytes = MessagePackSerializer.Serialize(original, Options);
        var deserialized = MessagePackSerializer.Deserialize<JobId>(bytes, Options);

        deserialized.Value.Should().Be(guid);
    }

    [Fact]
    public void RoundTrip_EmptyString()
    {
        var original = new JobId(string.Empty);

        var bytes = MessagePackSerializer.Serialize(original, Options);
        var deserialized = MessagePackSerializer.Deserialize<JobId>(bytes, Options);

        deserialized.Value.Should().BeEmpty();
    }
}
