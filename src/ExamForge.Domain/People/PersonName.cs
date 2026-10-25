using ExamForge.Domain.Common;

namespace ExamForge.Domain.People;

/// <summary>
/// ФИО. Отчество необязательно; дата рождения/адрес/паспорт/телефон/email не запрашиваются (ТЗ §4).
/// ФИО не уникальный ключ личности: однофамильцы не объединяются автоматически.
/// </summary>
public sealed record PersonName
{
    public string LastName { get; }
    public string FirstName { get; }
    public string? MiddleName { get; }

    private PersonName(string lastName, string firstName, string? middleName)
    {
        LastName = lastName;
        FirstName = firstName;
        MiddleName = middleName;
    }

    public static PersonName Create(string lastName, string firstName, string? middleName = null)
        => new(
            Guard.Required(lastName, "Фамилия", 100),
            Guard.Required(firstName, "Имя", 100),
            string.IsNullOrWhiteSpace(middleName) ? null : Guard.Required(middleName, "Отчество", 100));

    public string FullName =>
        MiddleName is null ? $"{LastName} {FirstName}" : $"{LastName} {FirstName} {MiddleName}";

    public override string ToString() => FullName;
}
