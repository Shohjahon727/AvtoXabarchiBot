using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvtoXabarchiBot.Core.Models
{
	public enum SubscriptionPlan
	{
		Trial,
		Start1Month,
		Pro6Month,
		ProMax12Month
	}

	public enum PaymentStatus
	{
		Pending,
		Paid,
		Expired,
		Cancelled
	}

	public class Subscription
	{
		public long Id { get; set; }
		public long UserId { get; set; }
		public SubscriptionPlan Plan { get; set; }
		public DateTime StartDate { get; set; }
		public DateTime EndDate { get; set; }
		public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;
		public decimal Amount { get; set; }
		public string? PaymentReceiptPath { get; set; }
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

		public User User { get; set; } = null!;
	}
}
