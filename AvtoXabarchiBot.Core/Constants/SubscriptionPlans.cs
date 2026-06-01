using AvtoXabarchiBot.Core.Models;

namespace AvtoXabarchiBot.Core.Constants;

public static class SubscriptionPlans
{
	public static readonly (SubscriptionPlan Plan, string Title, int Months, decimal Price)[] PaidPlans =
	[
		(SubscriptionPlan.Start1Month, "Start", 1, 15_000),
		(SubscriptionPlan.Pro6Month, "Pro", 6, 95_000),
		(SubscriptionPlan.ProMax12Month, "Pro max", 12, 165_000)
	];

	public static string GetPlanTitle(SubscriptionPlan plan) =>
		PaidPlans.FirstOrDefault(p => p.Plan == plan).Title ?? plan.ToString();

	public static int GetPlanMonths(SubscriptionPlan plan) =>
		PaidPlans.FirstOrDefault(p => p.Plan == plan).Months;

	public static decimal GetPlanPrice(SubscriptionPlan plan) =>
		PaidPlans.FirstOrDefault(p => p.Plan == plan).Price;

	public static string FormatInterval(int minutes)
	{
		if (minutes < 60) return $"har {minutes} daqiqada";
		if (minutes % 60 == 0)
		{
			var h = minutes / 60;
			return h == 1 ? "har 1 soatda" : $"har {h} soatda";
		}
		return $"har {minutes} daqiqada";
	}

	public static string FormatStatus(MessageStatus status) => status switch
	{
		MessageStatus.Active => "Faol",
		MessageStatus.Scheduled => "Rejalashtirilgan",
		MessageStatus.Paused => "To'xtatilgan",
		MessageStatus.Completed => "Yakunlangan",
		_ => status.ToString()
	};
}
