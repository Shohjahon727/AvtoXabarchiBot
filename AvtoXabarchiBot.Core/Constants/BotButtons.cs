namespace AvtoXabarchiBot.Core.Constants;

public static class BotButtons
{
	public const string SendMessage = "📩 Xabar yuborish";
	public const string MyMessages = "📋 Mening xabarlarim";
	public const string Subscriptions = "💎 Obunalarim";
	public const string AddAccount = "➕ Akkaunt qo'shish";
	public const string ShareContact = "📱 Raqamimni yuborish";
	public const string Cancel = "❌ Bekor qilish";
}

public static class CallbackData
{
	public const string Cancel = "cancel";
	public const string Back = "back";
	public const string ExtendSub = "extend";
	public const string ChangePlan = "change_plan";

	public static string MsgPause(long id) => $"msg_pause_{id}";
	public static string MsgResume(long id) => $"msg_resume_{id}";
	public static string MsgDelete(long id) => $"msg_delete_{id}";
	public static string MsgEdit(long id) => $"msg_edit_{id}";
	public static string MsgDetail(long id) => $"msg_detail_{id}";
	public static string MsgList = "msg_list";

	public static string GroupToggle(long accountId, long groupId) => $"grp_t_{accountId}_{groupId}";
	public static string GroupSelectAll(long accountId) => $"grp_all_{accountId}";
	public static string GroupContinue(long accountId) => $"grp_done_{accountId}";

	public static string Interval(int minutes) => $"int_{minutes}";
	public static string Plan(string plan) => $"plan_{plan}";

	public static string PayApprove(long subscriptionId) => $"pay_ok_{subscriptionId}";
	public static string PayReject(long subscriptionId) => $"pay_no_{subscriptionId}";
}
