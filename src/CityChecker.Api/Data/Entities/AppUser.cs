namespace CityChecker.Api.Data.Entities;

public class AppUser
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
