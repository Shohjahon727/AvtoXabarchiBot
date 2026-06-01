using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvtoXabarchiBot.Core.Models
{
	public enum MessageStatus
	{
		Draft,
		Scheduled,
		Active,
		Paused,
		Completed,
		Deleted
	}

	public class ScheduledMessage
	{
		public long Id { get; set; }
		public long UserId { get; set; }
		public long AccountId { get; set; }
		public string Content { get; set; } = string.Empty;
		public string? ImagePath { get; set; }
		public int IntervalMinutes { get; set; }
		public DateTime? NextSendAt { get; set; }
		public DateTime? LastSentAt { get; set; }
		public MessageStatus Status { get; set; } = MessageStatus.Draft;
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

		public User User { get; set; } = null!;
		public TelegramAccount Account { get; set; } = null!;
		public ICollection<MessageGroup> TargetGroups { get; set; } = new List<MessageGroup>();
	}
}
