namespace ModOrganizer.Core.Auth;

public interface IUserContext
{
    Guid? UserId { get; }
    string? Email { get; }
    string? DisplayName { get; }
    bool IsAuthenticated { get; }
    event EventHandler? UserChanged;
}

public sealed class AnonymousUserContext : IUserContext
{
    public Guid? UserId => null;
    public string? Email => null;
    public string? DisplayName => "anonymous";
    public bool IsAuthenticated => false;
#pragma warning disable CS0067
    public event EventHandler? UserChanged;
#pragma warning restore CS0067
}
