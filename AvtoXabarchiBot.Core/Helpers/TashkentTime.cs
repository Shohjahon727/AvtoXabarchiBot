namespace AvtoXabarchiBot.Core.Helpers;

/// <summary>
/// O'zbekiston vaqti (UTC+5, Asia/Tashkent).
/// Ma'lumotlar bazasida UTC saqlanadi, foydalanuvchiga Toshkent vaqtida ko'rsatiladi.
/// </summary>
public static class TashkentTime
{
	public const string DefaultFormat = "yyyy-MM-dd HH:mm";

	private static readonly TimeZoneInfo TashkentZone = ResolveTimeZone();

	public static DateTime UtcNow => DateTime.UtcNow;

	public static DateTime Now => ToTashkent(DateTime.UtcNow);

	public static DateTime ToTashkent(DateTime dateTime)
	{
		var utc = ToUtc(dateTime);
		return TimeZoneInfo.ConvertTimeFromUtc(utc, TashkentZone);
	}

	public static DateTime ToUtc(DateTime dateTime) => dateTime.Kind switch
	{
		DateTimeKind.Utc => dateTime,
		DateTimeKind.Local => dateTime.ToUniversalTime(),
		_ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
	};

	public static string Format(DateTime dateTime, string format = DefaultFormat) =>
		ToTashkent(dateTime).ToString(format);

	public static string FormatNullable(DateTime? dateTime, string format = DefaultFormat, string empty = "—") =>
		dateTime.HasValue ? Format(dateTime.Value, format) : empty;

	private static TimeZoneInfo ResolveTimeZone()
	{
		string[] ids =
		[
			"Asia/Tashkent",
			"Central Asia Standard Time",
			"West Asia Standard Time"
		];

		foreach (var id in ids)
		{
			try
			{
				return TimeZoneInfo.FindSystemTimeZoneById(id);
			}
			catch (TimeZoneNotFoundException) { }
			catch (InvalidTimeZoneException) { }
		}

		return TimeZoneInfo.CreateCustomTimeZone("Tashkent", TimeSpan.FromHours(5), "Toshkent", "Toshkent");
	}
}
