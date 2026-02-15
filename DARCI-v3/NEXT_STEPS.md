# DARCI Handoff (Current State)

Last updated: 2026-02-15

## Current goals
- Enable reliable Telegram inbound -> DARCI message pipeline.
- Keep notifications user-friendly and portable.
- Continue improving CAD generation for complex parts (gears/automotive).

## Implemented so far

### CAD integration
- Python CAD service integrated (`Darci.Python`).
- CAD bridge/client integrated in C# (`Darci.Tools/Cad`).
- CAD pipeline supports iterative correction and validation.
- Complex CAD prep added:
  - Complexity detection (`IsComplexCadRequest`).
  - Build-node planning step (`GenerateCadPlan`/`BuildCadPlanPrompt`).
  - Higher iteration budget for complex prompts.

### Notifications architecture
- Outgoing messages are dispatched via `ResponseDispatcher` (hosted service).
- `/responses` and `/responses/wait` use a response store (not direct channel reads).
- Providers:
  - Email (SMTP)
  - Telegram outbound
- Notification logs endpoint:
  - `GET /notifications/log`

### Telegram inbound
- `TelegramInboundService` added and registered as hosted service.
- Long-polls Telegram `getUpdates`.
- Filters by configured chat id.
- Forwards accepted text messages into `Awareness.NotifyMessage(...)`.

## Important recent change (notification policy)
- External provider notifications were changed to be message-policy-driven via:
  - `OutgoingMessage.ExternalNotify`
  - `SendMessage(..., externalNotify: bool)`
- Intended behavior:
  - Normal reply/chat messages: no external notification.
  - Completion-like notifications (e.g., explicit Notify actions, CAD success): external notification enabled.

## Current blocker
- Rebuilds were failing because `Darci.Api` was still running and locking DLLs.
- This likely means some recent changes were not loaded at runtime.
- Symptom seen: no Telegram inbound startup logs and no accepted inbound-message logs.

## Required restart sequence (important)
```powershell
Stop-Process -Name Darci.Api -Force
cd C:\Users\aiden\OneDrive\Documents\GitHub\ProgDS\DARCI-Live\DARCI-v3
dotnet build DARCI.sln
cd Darci.Api
dotnet run
```

## Quick runtime checks after restart

### 1) Telegram inbound status
`GET http://localhost:5080/telegram/inbound/status`

Expect:
- `enabled = true`
- `botTokenSet = true`
- `chatId` matches your Telegram chat id

### 2) Telegram inbound logs
Look for:
- `Telegram inbound bootstrap: ...`
- `Telegram inbound listener started for chat ...`
- `Accepted Telegram inbound message for DARCI from chat ...`

### 3) Notification behavior
`GET http://localhost:5080/notifications/log`

Expect:
- External notifications only for marked completion-style messages.
- Non-external messages should show skipped with reason.

## Environment variables in use

### Telegram
- `DARCI_TELEGRAM_BOT_TOKEN`
- `DARCI_TELEGRAM_CHAT_ID`

### SMTP (currently at least password env-backed)
- `DARCI_SMTP_PASSWORD`

## Next recommended tasks
1. Verify inbound service logs appear after clean restart.
2. Fully migrate all SMTP settings to env (host/port/user/from/to/enabled), not just password.
3. Confirm `ExternalNotify` policy is active by testing:
   - regular chat reply (should skip external notify)
   - completion-style message (should send)
4. Add offset persistence for Telegram inbound (optional, prevents reprocessing across restarts).

## Files touched recently (high-value)
- `Darci.Api/Program.cs`
- `Darci.Api/TelegramInboundService.cs`
- `Darci.Api/ResponseDispatcher.cs`
- `Darci.Tools/Notifications/*`
- `Darci.Tools/Toolkit.cs`
- `Darci.Tools/IToolkit.cs`
- `Darci.Shared/Models.cs`
- `Darci.Python/cad/cad_routes.py`

