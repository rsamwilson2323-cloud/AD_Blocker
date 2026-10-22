# 🛡️ AdBlocker — Windows + Chrome

A source-first Windows ad and tracker blocker built with **C#, .NET 8, WPF, PowerShell, and a Chrome Manifest V3 extension**. AdBlocker combines local DNS filtering with browser-side request filtering and YouTube ad-handling techniques in one lightweight application.

The project includes a minimal desktop control panel, a background Windows service, customizable filtering rules, and a Chrome extension designed to reduce advertising and tracking requests.

> **Important:** YouTube ad blocking is best-effort. YouTube can serve advertisements and normal videos through shared infrastructure, so no guarantee of blocking every pre-roll, mid-roll, or overlay ad is made.

---

## 📑 Table of Contents

- [Features](#-features)
- [Architecture Overview](#-architecture-overview)
- [Tech Stack](#-tech-stack)
- [Project Structure](#-project-structure)
- [How It Works](#-how-it-works)
- [Windows Application](#-windows-application)
- [Chrome Extension](#-chrome-extension)
- [DNS Filtering Engine](#-dns-filtering-engine)
- [Installation](#-installation)
- [Configuration](#-configuration)
- [Build and Testing](#-build-and-testing)
- [Troubleshooting](#-troubleshooting)
- [Limitations](#-limitations)
- [Privacy and Security](#-privacy-and-security)
- [Future Improvements](#-future-improvements)
- [Author](#-author)

---

## ✨ Features

### 🛡️ Advertising and Tracker Filtering
- Local DNS filtering for configured advertising and tracking domains.
- Chrome-side request filtering using Manifest V3 declarative rules.
- Customizable block and allow lists.
- YouTube ad-related request filtering and response pruning.
- Cosmetic cleanup for supported YouTube ad banners and overlays.
- Best-effort detection and handling of native skip controls.

### 🖥️ Minimal Windows Control Panel
- Simple, lightweight WPF desktop interface.
- Protection status and running timer.
- Start and Stop controls.
- Available filtering counters and connection status.
- Background service that operates independently of the launcher window.

### ⚙️ Background Protection
- Windows service for DNS filtering.
- Automatic service startup configuration.
- PowerShell scripts for installation, migration, and uninstallation.
- DNS configuration backup and restoration support.

### 🌐 Chrome Integration
- Chrome Manifest V3 extension.
- Extension status and local reporting integration.
- Browser-side filtering independent of the DNS engine.
- Unpacked extension support for development and testing.

---

## 🏗️ Architecture Overview

AdBlocker consists of three main components:

| Component | Technology | Responsibility |
|---|---|---|
| Desktop application | C#, WPF, .NET 8 | Displays protection status, timer, counters, and Start/Stop controls. |
| Windows service | C#, .NET 8 | Runs the local DNS filtering engine and manages configured filtering rules. |
| Chrome extension | JavaScript, Manifest V3 | Applies browser request rules and best-effort YouTube ad handling. |

### Architecture diagram

```text
                  ┌─────────────────────────┐
                  │       Chrome            │
                  │                         │
                  │ Manifest V3 Extension   │
                  │ Request Rules            │
                  │ YouTube Ad Handling      │
                  └────────────┬────────────┘
                               │
                               ▼
                  ┌─────────────────────────┐
                  │    Browser Filtering    │
                  │  Request Rules / Cleanup│
                  └─────────────────────────┘


┌─────────────────────┐       ┌─────────────────────┐
│   WPF Control Panel │──────▶│  Windows Service    │
│                     │       │                     │
│ Protection Status   │       │ Local DNS Proxy     │
│ Timer and Counters  │       │ Block/Allow Rules   │
│ Start / Stop        │       │ DNS Cache           │
└─────────────────────┘       └──────────┬──────────┘
                                         │
                                         ▼
                              ┌─────────────────────┐
                              │ Windows DNS Routing │
                              │ Local DNS: 127.0.0.1│
                              └─────────────────────┘
```

The Chrome extension and DNS engine perform different jobs. Starting the Windows service does not automatically prove that the Chrome extension is installed, enabled, or successfully filtering requests.

---

## 🧰 Tech Stack

### Windows Application and Engine
- **C#**
- **.NET 8**
- **WPF** — desktop user interface
- **Windows Service** — background execution
- **PowerShell** — setup, network configuration, and service management

### Browser Extension
- **JavaScript**
- **Chrome Extensions Manifest V3**
- **Declarative Net Request API**
- **Content scripts**
- **YouTube DOM cleanup and ad-state handling**

### Testing and Development
- Node.js
- Node.js built-in test runner
- .NET SDK
- Windows PowerShell

---

## 📁 Project Structure

```text
AdBlocker/
│
├── extension/
│   ├── manifest.json
│   ├── rules.json
│   ├── youtube-cleanup.js
│   ├── content scripts and styles
│   └── popup files
│
├── rules/
│   ├── block lists
│   └── allow lists
│
├── src/
│   ├── AdBlocker.Core/
│   │   ├── AdBlocker.Core.csproj
│   │   └── DNS parser, cache, and rule engine
│   │
│   ├── AdBlocker.Service/
│   │   ├── AdBlocker.Service.csproj
│   │   └── Background service and local control API
│   │
│   └── AdBlocker.App/
│       ├── AdBlocker.App.csproj
│       └── WPF control panel
│
├── extension-tests/
│   └── extension.test.js
│
├── Run.bat
├── Build.ps1
├── Install-Service.ps1
├── Uninstall-Service.ps1
├── Ad-Network.ps1
├── Migrate-From-SamAdBlocker.ps1
├── README.md
└── LICENSE
```

*The exact contents may differ between releases.*

---

## ⚙️ How It Works

### 1. DNS Filtering

The Windows service runs a local DNS proxy and evaluates domain lookups against configured block and allow rules.

When Windows DNS is routed through the local proxy, matching domains can be blocked before the browser connects to them.

DNS filtering operates at the domain level. It cannot inspect the encrypted contents of HTTPS pages.

### 2. Browser Request Filtering

The Chrome extension uses Manifest V3 request rules to block supported advertising and tracking requests.

Browser-side filtering can handle requests that DNS filtering alone cannot distinguish, depending on the request, rule, and browser API limitations.

### 3. YouTube Ad Handling

The extension uses a combination of techniques:

- Matching supported ad-related network requests.
- Pruning selected advertising data from supported responses.
- Detecting YouTube ad-player states.
- Attempting to use native skip controls.
- Cleaning up supported banner, overlay, and in-feed ad elements.

YouTube frequently changes its player, network requests, and advertising delivery. These techniques are best-effort and may require updates.

### 4. Protection Status and Counters

The desktop application displays the status reported by the background engine and available browser-extension metrics.

A running timer confirms elapsed runtime, not successful ad blocking. DNS blocks, browser rule matches, and ads actually prevented are different measurements.

---

## 🖥️ Windows Application

The desktop application is designed to keep the interface simple.

It provides:

- Current protection status.
- Protection uptime.
- Start and Stop controls.
- Available request counters.
- DNS and Chrome-extension connection status.

The background service operates independently of the `Run.bat` launcher. Closing the launcher does not stop the service. Use the application's Stop control to stop protection through the intended workflow.

---

## 🌐 Chrome Extension

AdBlocker uses a Chrome Manifest V3 extension.

### Install the unpacked extension

1. Extract the complete project ZIP.
2. Open Chrome and navigate to `chrome://extensions/`.
3. Enable **Developer mode**.
4. Click **Load unpacked**.
5. Select the project's `extension` folder containing `manifest.json`.
6. Confirm that AdBlocker appears in the extensions list and is enabled.
7. Refresh YouTube and test the extension.

### Updating an existing installation

If Chrome already has an unpacked copy of AdBlocker:

1. Back up the existing extension folder.
2. Update the files in that same folder.
3. Open `chrome://extensions/`.
4. Click **Reload** on AdBlocker.

Keeping the same extension folder and manifest identity helps preserve the existing extension ID.

---

## 🚀 Installation

### Prerequisites

- Windows 10 or Windows 11.
- .NET 8 SDK.
- Google Chrome.
- Administrator permissions for Windows service installation and DNS configuration.
- Node.js if you want to run the extension unit tests.

### Quick start

1. Download or clone the repository:

   ```bash
   git clone https://github.com/rsamwilson2323-cloud/AD_Blocker.git
   ```

2. Open the extracted project directory.

3. Right-click `Run.bat` and select **Run as administrator**.

4. Follow the setup prompts to build the application, install the service, and configure DNS filtering if desired.

5. Load the Chrome extension using the instructions above.

6. Open YouTube and test several videos.

### DNS configuration

System-wide DNS filtering is optional and affects applications that use the configured Windows DNS resolver.

The installer should back up the current adapter DNS configuration before making changes. If DNS filtering causes connectivity problems, use the project's supported restore or uninstall procedure.

---

## 🔧 Configuration

### DNS Rules

The `rules/` directory contains the project's DNS filtering lists.

Rules determine which domains are blocked or allowed. Keep allow rules specific, and avoid blocking infrastructure that also delivers ordinary website content or video.

### Chrome Rules

The `extension/` directory contains the Chrome extension's manifest, request-filtering rules, and YouTube-related scripts.

Changes to extension code generally require reloading the extension from `chrome://extensions/`.

### Service Management

The project provides PowerShell scripts for installing and uninstalling the Windows service.

Run administrative scripts only after reviewing their actions. DNS restoration depends on the availability of the saved configuration and the project's network helper.

---

## 🧪 Build and Testing

### Build the Windows application

Open PowerShell in the project directory:

```powershell
.\Build.ps1 -Test
```

If the build script is unavailable in your release, build the relevant .NET projects using the .NET 8 SDK and their `.csproj` files.

### Run the extension tests

```powershell
node --test extension-tests/extension.test.js
```

The automated tests cover selected extension rule and response-pruning behaviors.

**Important:** passing unit tests does not establish that live YouTube advertisements are blocked. The Windows build, DNS configuration, Chrome extension connection, and live browser behavior must be tested on a Windows machine.

---

## 🛠️ Troubleshooting

### Ads still appear on YouTube

- Confirm that AdBlocker is enabled in `chrome://extensions/`.
- Reload the extension after changing its files.
- Inspect the extension service worker for errors.
- Check whether browser rules are matching requests.
- Verify that YouTube cleanup scripts are running.
- Test pre-roll, mid-roll, and overlay behavior separately.

### Chrome extension is not detected

- Confirm that the extension is loaded and enabled.
- Check that its local reporting endpoint is available.
- Inspect service-worker errors and connection failures.
- Verify that the extension's folder and manifest are the expected versions.

### DNS proxy shows zero queries

- Confirm that the Windows service is running.
- Check whether Windows DNS is routed through `127.0.0.1`.
- Verify the configured DNS adapter and resolver settings.
- Remember that applications using a different resolver or their own DNS-over-HTTPS settings may bypass system DNS filtering.

### Websites or videos stop loading

- Review recently added DNS rules.
- Remove overly broad rules that may block shared content-delivery infrastructure.
- Restore the previous DNS settings if connectivity is disrupted.

---

## ⚠️ Limitations

- No guarantee of blocking every advertisement.
- YouTube in-stream advertising cannot be reliably blocked through DNS alone.
- YouTube may change its ad delivery and player behavior without notice.
- Browser request filtering depends on the supported APIs and current extension rules.
- Some advertising requests share infrastructure with normal video playback.
- The extension's counters indicate observed filtering activity, not a verified count of all ads prevented.
- Browser and Windows integration must be validated on the target machine.

The project intentionally avoids broadly blocking `googlevideo.com`, because doing so can interrupt normal video playback.

---

## 🔒 Privacy and Security

- The extension is designed to send numeric counters and its version to the local loopback API.
- Browsing history and page URLs are not intended to be sent to the local reporting API.
- DNS requests pass through the configured upstream resolvers.
- DNS filtering does not decrypt HTTPS traffic.
- Administrator permissions are required for service installation and system DNS changes.

Review the source code and configuration before running the project with elevated privileges.

---

## 🗺️ Future Improvements

Potential development directions include:

- More accurate browser-extension health reporting.
- Better diagnostics for matched and blocked requests.
- Improved rule-list management.
- Expanded automated browser tests.
- Regression testing against YouTube player changes.
- Clearer separation between DNS blocks, browser rule matches, and confirmed ad-handling actions.
- Improved recovery and rollback for DNS configuration changes.

---

## 📄 License

Add the project's chosen open-source license in the `LICENSE` file before publishing. Do not assume a license applies unless the repository includes it.

---

## 👨‍💻 Author

**R. Sam Wilson**

GitHub: https://github.com/rsamwilson2323-cloud

Repository: https://github.com/rsamwilson2323-cloud/AD_Blocker

---

*Built as an independent Windows and Chrome ad-blocking project focused on DNS filtering, browser request rules, and a lightweight desktop control panel.*