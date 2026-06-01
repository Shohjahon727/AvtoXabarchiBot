using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvtoXabarchiBot.Core.Models;

public class User
{
	public long Id { get; set; }
	public long TelegramId { get; set; }
	public string PhoneNumber { get; set; } = string.Empty;
	public string? FirstName { get; set; }
	public string? LastName { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime? TrialEndsAt { get; set; }
	public bool IsActive { get; set; } = true;

	public ICollection<TelegramAccount> Accounts { get; set; } = new List<TelegramAccount>();
	public ICollection<ScheduledMessage> Messages { get; set; } = new List< ScheduledMessage > ();
	public ICollection<Subscription> Subscriptions { get; set; } = new List< Subscription > ();
}
