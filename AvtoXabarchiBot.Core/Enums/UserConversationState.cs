namespace AvtoXabarchiBot.Core.Enums;

public enum UserConversationState
{
	None,
	WaitingAccountPhone,
	WaitingLoginCode,
	Waiting2FAPassword,
	WaitingMessageContent,
	WaitingMessageTextEdit,
	WaitingReceipt,
	WaitingPlanPayment
}
