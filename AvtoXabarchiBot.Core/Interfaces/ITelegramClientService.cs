namespace AvtoXabarchiBot.Core.Interfaces;

public enum LoginStep
{
	Completed,
	NeedsCode,
	NeedsPassword,
	Failed
}

public interface ITelegramClientService
{
	Task<LoginStep> BeginLoginAsync(string phoneNumber, string sessionPath, int apiId, string apiHash);
	Task<LoginStep> SubmitCodeAsync(string code);
	Task<LoginStep> SubmitPasswordAsync(string password);
	Task<bool> LoadSessionAsync(string sessionPath, int apiId, string apiHash, CancellationToken ct = default);
	Task<(bool Success, bool SessionExpired)> TryLoadSessionAsync(string sessionPath, int apiId, string apiHash, CancellationToken ct = default);
	Task<List<GroupInfo>> GetGroupsAsync();
	Task<bool> SendMessageToGroupAsync(long groupId, long accessHash, bool isChannel, string text, string? imagePath = null);
	Task DisconnectAsync();
	void Dispose();
}

public class GroupInfo
{
	/// <summary>WTelegram chats lug'atidagi kalit (odatda -100... format).</summary>
	public long Id { get; set; }
	public long AccessHash { get; set; }
	public string Title { get; set; } = string.Empty;
	public bool IsChannel { get; set; }
}
