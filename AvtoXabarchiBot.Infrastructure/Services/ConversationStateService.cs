using System.Collections.Concurrent;
using AvtoXabarchiBot.Core.Models;

namespace AvtoXabarchiBot.Infrastructure.Services;

public class ConversationStateService
{
	private readonly ConcurrentDictionary<long, UserSession> _sessions = new();

	public UserSession GetOrCreate(long telegramUserId) =>
		_sessions.GetOrAdd(telegramUserId, _ => new UserSession());

	public void Reset(long telegramUserId) =>
		_sessions.TryRemove(telegramUserId, out _);
}
