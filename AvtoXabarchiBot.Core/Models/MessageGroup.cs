using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvtoXabarchiBot.Core.Models
{
	public class MessageGroup
	{
		public long Id { get; set; }
		public long MessageId { get; set; }
		public long TelegramGroupId { get; set; }
		public string GroupTitle { get; set; } = string.Empty;

		public ScheduledMessage Message { get; set; } = null!;
	}
}
