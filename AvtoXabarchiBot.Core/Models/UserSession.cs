using AvtoXabarchiBot.Core.Enums;

namespace AvtoXabarchiBot.Core.Models;

public class UserSession
{
	public UserConversationState State { get; set; } = UserConversationState.None;
	public string? PendingPhone { get; set; }
	public long? PendingAccountId { get; set; }
	public long? EditingMessageId { get; set; }
	public SubscriptionPlan? PendingPlan { get; set; }

	public string? DraftText { get; set; }
	public string? DraftImagePath { get; set; }
	public HashSet<long> SelectedGroupIds { get; set; } = new();
	public long? DraftAccountId { get; set; }
}
