using ExamForge.Domain.Access;
using ExamForge.Domain.People;
using Xunit;

namespace ExamForge.UnitTests.Access;

public class InvitationCodeTests
{
    [Fact]
    public void Generate_ProducesFormattedCode_WithValidAlphabet()
    {
        var code = InvitationCode.Generate();
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
    }

    [Fact]
    public void Generate_IsNonSequential_AndUniqueInSample()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 1_000; i++)
            Assert.True(seen.Add(InvitationCode.Generate()));
    }

    [Fact]
    public void Normalize_StripsSeparatorsAndCase()
    {
        Assert.Equal("ABCD2345EFGH", InvitationCode.Normalize(" abcd-2345 efgh "));
    }

    [Theory]
    [InlineData("ABCD2345EFGH")]
    [InlineData("abcd2345efgh")]
    [InlineData("ABCD-2345-EFGH")]
    public void IsValidFormat_AcceptsCanonicalVariants(string input)
    {
        Assert.True(InvitationCode.IsValidFormat(InvitationCode.Normalize(input)));
    }

    [Theory]
    [InlineData("ABCD2345EFG")]     // короткий
    [InlineData("ABCD2345EFGHI")]    // длинный
    [InlineData("ABCD2345EFGO")]     // O excluded
    [InlineData("ABCD2345EFG0")]     // 0 excluded
    [InlineData("ABCD2345EFGI")]     // I excluded
    [InlineData("ABCD2345EFG1")]     // 1 excluded
    public void IsValidFormat_RejectsInvalidCodes(string input)
    {
        Assert.False(InvitationCode.IsValidFormat(InvitationCode.Normalize(input)));
    }

    [Fact]
    public void Hasher_SameCodeAndKey_ProducesSameStableHash()
    {
        var hasher = new InvitationCodeHasher(Convert.FromHexString("A1B2C3D4E5F60718293A4B5C6D7E8F90"));
        var a = hasher.ComputeHash("ABCD2345EFGH");
        var b = hasher.ComputeHash("ABCD2345EFGH");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.DoesNotContain("ABCD2345EFGH", a);
    }

    [Fact]
    public void Hasher_DifferentKey_ProducesDifferentHash()
    {
        var h1 = new InvitationCodeHasher(Convert.FromHexString("A1B2C3D4E5F60718293A4B5C6D7E8F90"));
        var h2 = new InvitationCodeHasher(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));
        Assert.NotEqual(h1.ComputeHash("ABCD2345EFGH"), h2.ComputeHash("ABCD2345EFGH"));
    }

    [Fact]
    public void Hasher_ShortKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new InvitationCodeHasher(new byte[8]));
    }
}

public class PersonNameTests
{
    [Fact]
    public void Create_WithoutMiddleName_IsValid()
    {
        var name = PersonName.Create("Иванова", "Анна");
        Assert.Null(name.MiddleName);
        Assert.Equal("Иванова Анна", name.FullName);
    }

    [Fact]
    public void Create_WithMiddleName_IncludesItInFullName()
    {
        var name = PersonName.Create("Иванова", "Анна", "Сергеевна");
        Assert.Equal("Иванова Анна Сергеевна", name.FullName);
    }

    [Fact]
    public void Create_TrimsWhitespace()
    {
        var name = PersonName.Create("  Иванова  ", "Анна");
        Assert.Equal("Иванова", name.LastName);
    }

    [Theory]
    [InlineData("", "Анна")]
    [InlineData("Иванова", " ")]
    public void Create_EmptyRequiredParts_Throw(string last, string first)
    {
        Assert.Throws<ExamForge.Domain.Common.DomainException>(() => PersonName.Create(last, first));
    }
}
