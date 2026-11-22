using System.ComponentModel.DataAnnotations;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Web.Models;

public class UpdateProfileViewModel
{
    [Required(ErrorMessage = "Display name is required.")]
    [StringLength(64, ErrorMessage = "Display name cannot exceed 64 characters.")]
    [Display(Name = "Display Name")]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Bio cannot exceed 500 characters.")]
    [Display(Name = "Bio")]
    public string? Bio { get; set; }
}

public sealed class ProfileViewModel : UpdateProfileViewModel
{
    public UserId? Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? StatusMessage { get; set; }
}
