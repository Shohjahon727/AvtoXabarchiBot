using System.Collections.Concurrent;
using AvtoXabarchiBot.Core.Interfaces;

namespace AvtoXabarchiBot.Infrastructure.Services;

/// <summary>
/// Faol login jarayonidagi WTelegram clientlarni saqlaydi.
/// </summary>
public class TelegramClientSessionStore
{
	private readonly ConcurrentDictionary<long, TelegramClientService> _loginClients = new();

	public TelegramClientService GetOrAddLoginClient(long telegramUserId, Func<TelegramClientService> factory) =>
		_loginClients.GetOrAdd(telegramUserId, _ => factory());

	public bool TryGetLoginClient(long telegramUserId, out TelegramClientService? client) =>
		_loginClients.TryGetValue(telegramUserId, out client);

	public bool TryTakeLoginClient(long telegramUserId, out TelegramClientService? client) =>
		_loginClients.TryRemove(telegramUserId, out client);

	public void RemoveLoginClient(long telegramUserId)
	{
		if (_loginClients.TryRemove(telegramUserId, out var client))
			client.Dispose();
	}
}
