namespace ExamForge.Domain.Common;

/// <summary>Нарушение доменного инварианта. Не содержит персональных данных и ответов.</summary>
public sealed class DomainException(string message) : Exception(message);
