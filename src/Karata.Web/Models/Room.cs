#nullable enable

using Karata.Web.Models.UI;

namespace Karata.Web.Models;

public class Room
{
    public int Id { get; set; }
    public string? InviteLink { get; set; }
    public virtual User? Creator { get; set; } 
    public virtual Game Game { get; set; } = new ();
    public DateTime CreatedAt { get; } = DateTime.Now;
    public byte[]? Hash { get; set; } = null;
    public byte[]? Salt { get; set; } = null;
    public virtual ICollection<Chat> Chats { get; set; } = new List<Chat>();

    public UIRoom ToUI() => new()
    {
        InviteLink = InviteLink,
        CreatedAt = CreatedAt,
        Creator = Creator?.ToUI(),
        Game = Game.ToUI(),
        Chats = Chats.Select(c => c.ToUI()).ToList()
    };
}