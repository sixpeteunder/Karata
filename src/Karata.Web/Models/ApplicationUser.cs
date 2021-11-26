#nullable enable

using Microsoft.AspNetCore.Identity;

namespace Karata.Web.Models;

public class ApplicationUser : IdentityUser {
    public virtual List<Hand> Hands { get; set; } = new();
    public virtual List<Turn> Turns { get; set; } = new();
}