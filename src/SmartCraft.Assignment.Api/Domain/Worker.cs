namespace SmartCraft.Assignment.Api.Domain;

/// <summary>
/// A person who performs work. Deliberately minimal: no global billing rate
/// (billing terms are contextual to a <see cref="ProjectAssignment"/>).
/// </summary>
public sealed class Worker
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;

    private Worker()
    {
    }

    public static Worker Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException(DomainErrorKind.Validation, "Name must not be empty.");
        }

        return new Worker
        {
            Id = Guid.NewGuid(),
            Name = name,
        };
    }
}
