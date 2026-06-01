namespace AvtoXabarchiBot.Core.Models;

public class AccountGroup
{
	public long Id { get; set; }
	public long AccountId { get; set; }
	public long TelegramGroupId { get; set; }
	public long AccessHash { get; set; }
	public string Title { get; set; } = string.Empty;
	public bool IsChannel { get; set; }

	public TelegramAccount Account { get; set; } = null!;
}
