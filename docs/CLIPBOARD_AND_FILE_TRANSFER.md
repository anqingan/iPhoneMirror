# Clipboard text and LAN transfer

The mirroring toolbar now includes **Transfer text and files**. This opens a temporary transfer window. Choose the computer's private IPv4 address on the same LAN as the iPad and click **Start / reconnect**. Paste the connection link into Safari on the iPad. The link's fragment supplies the six-digit pairing code; the page removes it from the address bar after reading it. Alternatively, enter the code shown on the computer.

The transfer window has its own taskbar entry and stays visible when the main window is minimized or hidden to the tray. Opening it again from the toolbar restores the existing window if minimized and preserves the current session. Closing the transfer window or exiting the application stops the session.

On the iPad page, paste copied text into **Send to computer** and click **Send text**. The computer displays the received text and, by default, copies it to its clipboard. Automatic clipboard writes preserve a newer local copy and retry when the Windows clipboard is temporarily busy; **Copy received text** is available as a manual fallback.

In the other direction, type text in the computer window or explicitly read its clipboard, then click **Share this text**. Refresh the iPad page to receive it. The page offers a Copy button, with manual text selection as a fallback when Safari restricts clipboard access on a local HTTP page.

For pictures or arbitrary files, choose files on the computer and download them from the iPad page, or use the page's file picker to upload from iPad Photos or Files. Uploaded files appear in the computer window. **Open received files folder** opens their saved location. **Share clipboard image** exports a Windows clipboard bitmap as PNG. **Copy selected image** copies a compatible received PNG/JPEG/BMP/GIF/TIFF to the Windows clipboard, with a 4096-pixel maximum edge; other image formats can be saved and opened as files. This is file-based image transfer, not automatic synchronization of the iPad image clipboard.

Each file is limited to 512 MiB; one session is limited to 2 GiB of shared/received data and 100 files. Duplicate names never replace received files. Outgoing files are explicit snapshots, so later local changes do not alter a published download. Received files persist under the user's local `iPhoneMirror/Transfers` application-data directory. Closing the window stops its listener and removes outgoing snapshots. Pairing expires after 30 minutes; reconnecting creates a new session. No cloud relay is used. Local HTTP transfers should be used on a trusted LAN. Windows Firewall may require allowing this application on a private network.

## Native iPad text clipboard

The existing USB/wireless control bridge continues to poll the iPad text clipboard. In addition to inline plain text, it can now extract text from an inline HTML or WebKit webarchive representation. This helps with copies from apps that immediately supply rich content but leave plain text promised. It does not fetch external HTML resources or include script/style content. Inline plain text keeps precedence.

Promised-only content is still dependent on the device's CoreDevice service. Native clipboard compatibility with every iPadOS application is not guaranteed; the explicit Safari text-transfer path is available when native reading fails. The service behavior motivating the rich-text fallback is documented in the upstream [pymobiledevice3 pasteboard implementation](https://github.com/doronz88/pymobiledevice3/blob/136bb30fae802265da3d8524ee01cd8f666d1e9a/pymobiledevice3/remote/core_device/pasteboard_service.py).

## Validation

`dotnet <runtime-test-assembly> --lan-transfer` runs an actual loopback HTTP server and checks pairing, foreign origins/hosts, unauthenticated access, Unicode text and filenames, exact PNG bytes in both directions, outgoing snapshot isolation, unique received paths, file/session quotas, pairing attempt limits, persistence of received files, and listener shutdown. It is also called by the default runtime test entry point.

Python clipboard tests cover HTML/webarchive extraction, plain-text precedence and malformed rich data, alongside the existing clipboard races and cancellation tests. Synthetic local HTTP transfer and desktop window reception have been verified. These checks do not constitute a completed end-to-end Safari test on the user's iPad.
