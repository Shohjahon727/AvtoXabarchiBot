# Telegram API ID va API Hash olish

**Akkaunt qo'shish** (guruhlarga xabar yuborish) uchun `ApiId` va `ApiHash` kerak. Bot tokeni buning uchun yetmaydi.

## Qadamlar

1. Brauzerda oching: **https://my.telegram.org**
2. Telefon raqamingizni kiriting (Telegram akkauntingizdagi raqam).
3. Telegram ilovasiga kelgan kodni kiriting.
4. **"API development tools"** bo'limiga kiring.
5. Agar so'rasa, yangi ilova yarating:
   - **App title:** `Avto Xabarchi` (istalgan nom)
   - **Short name:** `avtoxabarchi` (lotin, kamida 5 belgi)
   - **Platform:** Desktop yoki Other
6. Sahifada ko'rasiz:
   - **App api_id** — raqam (masalan `28475612`)
   - **App api_hash** — matn (masalan `a1b2c3d4e5f6...`)

## Loyihaga qo'shish

`AvtoXabarchiBot.Web/appsettings.json` ichida:

```json
"Telegram": {
  "BotToken": "...",
  "ApiId": 28475612,
  "ApiHash": "sizning_api_hashingiz"
}
```

`ApiId` raqam, qo'shtirnoqsiz. `ApiHash` qo'shtirnoq ichida.

So'ng botni qayta ishga tushiring.

## Admin

- **AdminUserIds:** `730234941` (@alijonovsh) — kvitansiyani tasdiqlash/rad etish
- Ixtiyoriy: to'lovlar guruhi uchun `Payment:AdminChatId` (guruh ID, minus bilan)
