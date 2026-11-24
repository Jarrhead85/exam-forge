using ExamForge.Domain.Common;
using ExamForge.Domain.Dictionary;
// Тестовое namespace-дерево содержит namespace Dictionary → простое имя типа затенено; alias.
using OwnedDictionary = ExamForge.Domain.Dictionary.Dictionary;

namespace ExamForge.UnitTests.DictionaryDomain;

/// <summary>
/// P0-08: домен владения словарём — создание от имени преподавателя (Private),
/// legacy-создание без владельца (общедоступное прежнее поведение), смена режима.
/// Проверки доступа выполняет DictionaryService (требует БД — покрытие интеграционными тестами).
/// Простое имя «Dictionary» внутри UnitTests конфликтует с namespace UnitTests.Dictionary,
/// поэтому доменный тип используется через alias.
/// </summary>
public sealed class DictionaryOwnershipTests
{
    private readonly Guid _subjectId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithOwner_IsPrivateAndOwned()
    {
        var owner = Guid.NewGuid();
        var dictionary = OwnedDictionary.Create(_subjectId, "Латинский словарь", Now, owner);

        Assert.Equal(owner, dictionary.OwnerTeacherId);
        Assert.Equal(OwnedDictionary.AccessMode.Private, dictionary.Mode);
        Assert.Equal("Латинский словарь", dictionary.Name);
        Assert.Equal(Now, dictionary.CreatedAtUtc);
    }

    [Fact]
    public void Create_WithoutOwner_IsLegacyPublic()
    {
        // До P0-08 словари создавались без владельца: такое создание остаётся допустимым
        // (seed/старые данные) и означает Guid.Empty — общее поведение (инвариант политики).
        var dictionary = OwnedDictionary.Create(_subjectId, "Общий словарь", Now);

        Assert.Equal(Guid.Empty, dictionary.OwnerTeacherId);
    }

    [Fact]
    public void Rename_DoesNotChangeOwnerOrMode()
    {
        var owner = Guid.NewGuid();
        var dictionary = OwnedDictionary.Create(_subjectId, "Старое имя", Now, owner);
        dictionary.Rename("Новое имя");

        Assert.Equal("Новое имя", dictionary.Name);
        Assert.Equal(owner, dictionary.OwnerTeacherId);
        Assert.Equal(OwnedDictionary.AccessMode.Private, dictionary.Mode);
    }

    [Fact]
    public void SetAccessMode_StoresExplicitly()
    {
        var dictionary = OwnedDictionary.Create(_subjectId, "Разданный", Now, Guid.NewGuid());

        dictionary.SetAccessMode(OwnedDictionary.AccessMode.SharedRead);
        Assert.Equal(OwnedDictionary.AccessMode.SharedRead, dictionary.Mode);

        dictionary.SetAccessMode(OwnedDictionary.AccessMode.SharedEdit);
        Assert.Equal(OwnedDictionary.AccessMode.SharedEdit, dictionary.Mode);
    }

    [Fact]
    public void Grant_RecordsGranterAndMoment()
    {
        var grantedBy = Guid.NewGuid();
        var grant = DictionaryGrant.Create(Guid.NewGuid(), Guid.NewGuid(), grantedBy, Now);

        Assert.Equal(grantedBy, grant.GrantedByTeacherId);
        Assert.Equal(Now, grant.GrantedAtUtc);
    }

    [Fact]
    public void Create_EmptyName_Rejected()
    {
        Assert.Throws<DomainException>(() => OwnedDictionary.Create(_subjectId, " ", Now, Guid.NewGuid()));
    }
}
