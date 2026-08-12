# WeChat UI Automation Compatibility Report

> Status: initial probe  
> Date: 2026-08-11  
> Host: Windows 10 build 26200, x64

## Tested Client

| Item | Value |
| --- | --- |
| Executable | `C:\Program Files\Tencent\Weixin\Weixin.exe` |
| Product | Weixin |
| Version | `4.1.11.55` |
| Main window | Detected |
| Window capture | Passed at `1125 x 753` logical pixels |

## Standard UIA Probe

The standard accessibility tree exposed only these structural nodes:

```text
Window
  Pane: Weixin
  Pane: MMUIRenderSubWindowHW
```

Contacts, conversations, messages, the editor, and toolbar controls were not exposed as individual UIA elements. Pure selector-based UI Automation is therefore not sufficient for this client version.

## Compatibility Decision

`4.1.11.55` is classified as `HybridRecognitionRequired` and remains read-only until the hybrid adapter passes its acceptance suite.

The adapter must combine:

1. UIA and Win32 checks for process, window identity, foreground state, bounds, and modal detection.
2. Window capture with OCR or visual anchors for regions that are not exposed through accessibility.
3. A second target-identity check immediately before every mutating action.
4. Post-action visual confirmation and an `Unknown` result when confirmation is inconclusive.
5. A pinned DPI, language, theme, layout, and client-version profile.

## Safety Gate

Mutating operations stay disabled until all of the following pass:

- Contact and group identity can be resolved without relying on a duplicated display name.
- `@bot`, `@all`, other mentions, and ordinary text can be distinguished in the supported layout.
- The adapter produces zero wrong-target actions and zero duplicate actions in the controlled test set.
- Window obstruction, lock screen, unknown dialog, layout drift, and version drift cause a closed failure.
- Pre-action and post-action evidence is retained with redaction and a bounded retention period.

No hook, injection, process-memory access, protocol reverse engineering, or security-control bypass is permitted as a fallback.

