using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvtoXabarchiBot.Core.Models
{
	public class TelegramAccount
	{
		public long Id { get; set; }
		public long UserId { get; set; }
		public string PhoneNumber { get; set; } = string.Empty;
		public string SessionData { get; set; } = string.Empty; // TDLib session JSON
		public bool IsConnected { get; set; }
		public bool Has2FA { get; set; }
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
		public DateTime? LastUsedAt { get; set; }

		public User User { get; set; } = null!;
		public ICollection<ScheduledMessage> Messages { get; set; } = new List<ScheduledMessage>();
		public ICollection<AccountGroup> Groups { get; set; } = new List<AccountGroup>();
	}
}
